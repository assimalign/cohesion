using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlSortSpillConformanceTests : IDisposable
{
    private const int seed = 178903;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-sort-spill-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public SqlSortSpillConformanceTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill conformance: root sort agrees with an in-memory reference including stable representatives (#1789)")]
    public async Task ExecuteAsync_DifferentialCorpus_ShouldPreserveEvaluationAndRows()
    {
        // Arrange
        _output.WriteLine($"SEED={seed}");
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await Create("reference", 64 * 1024 * 1024, logs[0]);
        await using var spilled = await Create("spilled", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        string[] queries =
        [
            "SELECT id,k FROM t ORDER BY k",
            "SELECT id,k,(SELECT 1) AS scalar FROM t ORDER BY k",
            "WITH one AS (SELECT 1 AS x) SELECT id,k FROM t ORDER BY k",
            "SELECT id,k FROM t ORDER BY k DESC NULLS FIRST,id DESC",
            "SELECT id,k FROM t ORDER BY k NULLS LAST LIMIT 2500 OFFSET 13",
            "SELECT id,k FROM t ORDER BY k OFFSET 6000",
            "SELECT id,k FROM t ORDER BY k LIMIT 0",
            "SELECT id,k FROM t WHERE id<0 ORDER BY k",
            "SELECT id,k FROM t ORDER BY 2 DESC,1",
            "SELECT id AS alias,k FROM t ORDER BY alias DESC LIMIT 2500",
            "SELECT id,note FROM t ORDER BY note,id DESC",
            "SELECT DISTINCT note FROM t ORDER BY id DESC LIMIT 1",
            "SELECT DISTINCT k FROM t ORDER BY id DESC LIMIT 2 OFFSET 1",
            "SELECT id,CAST(note AS CHAR(10)) FROM t ORDER BY CAST(note AS CHAR(10))",
            "SELECT id,CAST(id AS DECIMAL(10,2)) FROM t ORDER BY CASE WHEN id%2=0 THEN CAST(k AS DECIMAL(10,2)) ELSE CAST(k AS DOUBLE PRECISION) END",
            "SELECT id,CAST('{\"x\":1}' AS JSONB),X'00FF' FROM t ORDER BY k",
            "SELECT id,CAST('{\"x\":1}' AS JSONB) AS j FROM t ORDER BY j,k DESC",
            "SELECT id,wide(id) AS value FROM t ORDER BY value DESC",
            "SELECT stamp(id) AS observed,k FROM t ORDER BY observed DESC LIMIT 2500 OFFSET 9",
            "SELECT stamp(id),k FROM t ORDER BY k DESC LIMIT 2500 OFFSET 9",
            "SELECT stamp(id),k FROM t WHERE id<200 ORDER BY wide(id)",
            "SELECT stamp(id),k FROM t WHERE id<1000 ORDER BY nextval('s')",
            "SELECT DISTINCT stamp(k) FROM t ORDER BY id DESC LIMIT 2 OFFSET 1",
            "SELECT id FROM t ORDER BY stamp(k) DESC",
            "SELECT nextval('s') AS serial,id FROM t ORDER BY serial DESC LIMIT 2500",
            "SELECT t.id,t.k FROM t CROSS JOIN u ORDER BY t.k DESC,t.id",
            "SELECT DISTINCT t.note FROM t CROSS JOIN u ORDER BY t.id DESC LIMIT 1",
            "SELECT stamp(t.id),t.k FROM t CROSS JOIN u ORDER BY t.k DESC,t.id",
            "SELECT id,k FROM t WHERE id>=@min ORDER BY k DESC LIMIT @count OFFSET @skip",
        ];

        // Act / Assert: empty and LIMIT 0 stay in memory; every other ordinary sort must spill.
        foreach (string sql in queries)
        {
            logs[0].Clear(); logs[1].Clear();
            var parameters = new Dictionary<string, object?> { ["min"] = 0, ["count"] = 2500L, ["skip"] = 10L };
            await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql, parameters);
            await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql, parameters);
            var expected = await Rows(left);
            (await Rows(right)).ShouldBe(expected, customMessage: sql);
            right.Columns.Select(c => (c.Name, c.Type, c.IsNullable, c.MaxLength)).ShouldBe(left.Columns.Select(c => (c.Name, c.Type, c.IsNullable, c.MaxLength)));
            logs[1].ShouldBe(logs[0], customMessage: sql);
            var am = a.LastStatementMetrics!; var bm = b.LastStatementMetrics!;
            bm.RecordsExamined.ShouldBe(am.RecordsExamined); bm.JoinCandidatePairs.ShouldBe(am.JoinCandidatePairs);
            bm.AccessPath.ShouldBe(am.AccessPath); bm.HashMemoryBytes.ShouldBe(0); bm.PlainSelectMemoryBytes.ShouldBe(0);
            bm.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(262144);
            bool mustSpill = !sql.Contains("LIMIT 0", StringComparison.Ordinal) && !sql.Contains("id<0", StringComparison.Ordinal);
            if (mustSpill) { right.ShouldBeOfType<SqlSpilledResultSet>(); SqlSortSpillMetrics.For(bm).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0); }
            else { right.ShouldBeOfType<SqlMaterializedResultSet>(); }
            _output.WriteLine($"DIFFERENTIAL|{sql}|spill={right is SqlSpilledResultSet}|rows={expected.Length}");
        }
        await using var sequenceReference = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var sequenceSpill = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(sequenceSpill)).ShouldBe(await Rows(sequenceReference));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill lifetime: rewind and concurrent delivery survive later statements; all owners release (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GetRowsAsync_RepeatedDelivery_ShouldReleaseAtEachOwner(int disposeOwner)
    {
        // Arrange
        await using var engine = await Create("lifetime", 262144);
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await Seed(session);
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k DESC LIMIT 17 OFFSET 200");
        result.ShouldBeOfType<SqlMaterializedResultSet>(); // bounded heap fits; warm the fitting path
        result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k DESC LIMIT 2500 OFFSET 200");
        result.ShouldBeOfType<SqlSpilledResultSet>();
        long before = TempBytes(engine.TempSpace!); before.ShouldBeGreaterThan(0);
        Files(engine).ShouldBeGreaterThan(0);

        // Act
        var first = await Rows(result);
        var concurrent = await Task.WhenAll(Rows(result), Rows(result));
        await session.ExecuteAsync("DELETE FROM t");
        (await Rows(result)).ShouldBe(first);
        if (disposeOwner == 0) { await result.DisposeAsync(); }
        else if (disposeOwner == 1) { await session.DisposeAsync(); }
        else { await engine.DisposeAsync(); }

        // Assert
        foreach (var rows in concurrent) { rows.ShouldBe(first); }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await Rows(result));
        await result.DisposeAsync(); TempBytes(engine.TempSpace!).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill admission: missing directory, small grants and nested roots keep CS05067 (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ExecuteAsync_UnavailablePermission_ShouldRefuseAndRelease(int refusal)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("refusals");
        builder.Options.StatementMemoryBudget = refusal == 1 ? 4096 : 262144;
        if (refusal != 0) { builder.Options.TempDirectory = Path.Combine(_root, "memory"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        string sql = refusal switch
        {
            2 => "SELECT COUNT(*) FROM (SELECT id,k FROM t ORDER BY k) d",
            3 => "WITH d AS (SELECT id,k FROM t ORDER BY k) SELECT COUNT(*) FROM d",
            4 => "(SELECT id FROM t ORDER BY k) UNION ALL SELECT id FROM t",
            5 => "CREATE TABLE c AS SELECT id,k FROM t ORDER BY k",
            6 => "SELECT id,COUNT(*) OVER() FROM (SELECT id,k FROM t ORDER BY k) d",
            7 => "SELECT t.id,(SELECT u.id FROM u WHERE u.id=t.id) AS value FROM t ORDER BY t.k",
            8 => "WITH RECURSIVE r(id) AS (SELECT 0 UNION ALL SELECT id+1 FROM r WHERE id<4000) SELECT id FROM r ORDER BY id",
            10 => "SELECT k,COUNT(*) FROM t GROUP BY GROUPING SETS ((k)) ORDER BY k",
            11 => "SELECT nextval('s'),id FROM t ORDER BY k",
            _ => "SELECT id,k FROM t ORDER BY k",
        };

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
        failure.Diagnostics.ShouldContain(d => d.Code == (refusal == 6 ? "CS05079" : "CS05067"));
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        if (engine.TempSpace is not null) { TempBytes(engine.TempSpace).ShouldBe(0); Files(engine).ShouldBe(0); }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill exclusion: INSERT SELECT retains its unbudgeted source policy pending #1828 (#1789)")]
    public async Task ExecuteAsync_InsertSource_ShouldKeepExistingUnspilledPolicy()
    {
        // Arrange: _isDml bypasses SELECT charging; this run must not silently start #1828.
        await using var engine = await Create("insert-source-policy", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session); await session.ExecuteAsync("CREATE TABLE c(id INT,k INT)");

        // Act
        var result = await session.ExecuteAsync("INSERT INTO c SELECT id,k FROM t ORDER BY k");

        // Assert
        result.AffectedCount.ShouldBe(4000);
        SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    private async Task<SqlDatabaseEngine> Create(string name, long budget, List<long>? log = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.RootPath = Path.Combine(_root, name);
        builder.Options.StatementMemoryBudget = budget;
        builder.Options.StatementTempSpaceQuota = 64 * 1024 * 1024;
        builder.Options.CheckpointJournalSize = 65536;
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { log?.Add(value); return value; }));
        builder.Functions.Add(SqlScalarFunction.Create("wide", (long value) => { log?.Add(-1000000 - value); return new string((char)('a' + value % 23), 1100); }));
        return await builder.BuildAsync();
    }
    private static async Task Seed(SqlDatabaseSession session, int count = 4000)
    {
        await session.ExecuteAsync("CREATE TABLE t(id INT,k INT,note TEXT COLLATE case_insensitive)");
        var random = new Random(seed);
        for (int first = 0; first < count; first += 200)
        {
            var rows = Enumerable.Range(first, Math.Min(200, count - first)).Select(i =>
                $"({i},{(i % 31 == 0 ? "NULL" : random.Next(73).ToString(System.Globalization.CultureInfo.InvariantCulture))},'{(i % 2 == 0 ? "a" : "A")}')");
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', rows));
        }
        await session.ExecuteAsync("CREATE TABLE u(id INT)"); await session.ExecuteAsync("INSERT INTO u VALUES(1)");
        await session.ExecuteAsync("CREATE SEQUENCE s");
    }
    private static async Task<string[]> Rows(DatabaseResultSet result, CancellationToken token = default)
    {
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(token))
        { values.Add(string.Join('|', Enumerable.Range(0, row.FieldCount).Select(i => row.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : row.GetValue(i)?.ToString() ?? "<NULL>"))); }
        return values.ToArray();
    }
    private static int Files(SqlDatabaseEngine engine) => Directory.Exists(engine.TempSpace!.DirectoryPath) ? Directory.GetFiles(engine.TempSpace.DirectoryPath, "*.ctmp").Length : 0;
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_bytes")]
    private static extern ref long TempBytes(StorageTempSpace space);
}
