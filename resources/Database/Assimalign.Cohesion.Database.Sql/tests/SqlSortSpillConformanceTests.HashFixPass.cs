using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Hash proof: pseudo-type sums match the original path at both grants (#1789)")]
    [InlineData(false, 67108864)]
    [InlineData(true, 67108864)]
    [InlineData(false, 262144)]
    [InlineData(true, 262144)]
    public async Task ExecuteAsync_HashPseudoTypeBound_ShouldMatchOriginalRefusal(bool pipeline, long budget)
    {
        // Arrange: parameter insertion preserves the failing row's original scan position.
        var logs = new[] { new List<long>(), new List<long>() };
        var original = SqlDatabaseEngine.CreateBuilder("proof-original");
        original.Options.StatementMemoryBudget = budget;
        original.Options.StatementTempSpaceQuota = 67108864;
        original.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { logs[0].Add(value); return value; }));
        await using var reference = await original.BuildAsync();
        await using var spilling = await Create("proof-spilling", budget, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilling.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await session.ExecuteAsync("CREATE TABLE t(id INT,d DOUBLE PRECISION)");
            for (int first = 0; first < 8000; first += 200)
            {
                await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 200)
                    .Select(i => $"({i},{(i == 4317 ? "@d" : "1.0")})")), new Dictionary<string, object?> { ["d"] = 1e300 });
            }
        }
        string sql = pipeline
            ? "SELECT id + 1/(id-5000), SUM(ROUND(d)) FROM t GROUP BY id + 1/(id-5000)"
            : "SELECT stamp(id), SUM(ROUND(d)) FROM t GROUP BY stamp(id)";

        // Act
        var left = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
        var right = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));

        // Assert: unsupported spilling retains the ORIGINAL refusal at the SAME budget.
        right.Diagnostics[0].Code.ShouldBe(left.Diagnostics[0].Code);
        right.Message.ShouldBe(left.Message); logs[1].ShouldBe(logs[0]);
        right.Diagnostics[0].Code.ShouldBe(budget == 67108864 ? "CS02002" : "CS05067");
        if (!pipeline && budget == 67108864) { logs[1].Count.ShouldBe(4318); }
        b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
        SqlHashSpillMetrics.For(b.LastStatementMetrics).ShouldBeNull();
        TempBytes(spilling.TempSpace!).ShouldBe(0);
        _output.WriteLine($"PROOF|pipeline={pipeline}|budget={budget}|code={right.Diagnostics[0].Code}|calls={logs[1].Count}|examined={b.LastStatementMetrics.RecordsExamined}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash extrema: text and binary keep their fixed-state refusal (#1789)")]
    [InlineData(false, 262144)]
    [InlineData(false, 262208)]
    [InlineData(false, 67108864)]
    [InlineData(true, 262144)]
    [InlineData(true, 262208)]
    [InlineData(true, 67108864)]
    public async Task ExecuteAsync_HashVariableExtreme_ShouldRefuseAtBothSmallGrants(bool binary, long budget)
    {
        // Arrange
        await using var engine = await Create("extreme-bound", budget);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        await session.ExecuteAsync("UPDATE t SET note=@name", new Dictionary<string, object?> { ["name"] = new string('x', 100) });
        string sql = binary ? "SELECT id,MAX(CAST(@binary AS VARBINARY(100))) FROM t GROUP BY id" : "SELECT id,MAX(note) FROM t GROUP BY id";

        var parameters = new Dictionary<string, object?> { ["binary"] = new byte[100] };

        // Act / Assert
        if (budget == 67108864)
        { await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters); (await Rows(result)).Length.ShouldBe(8000); }
        else
        { var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, parameters)); error.Diagnostics[0].Code.ShouldBe("CS05067"); }
        SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull(); TempBytes(engine.TempSpace!).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash COUNT: a long argument spills only its null flag (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashCountPayload_ShouldRetainOnlyNullness(bool filtered)
    {
        // Arrange
        var log = new List<long>();
        await using var engine = await Create("count-payload", 262144, log);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        var parameters = new Dictionary<string, object?> { ["long"] = new string('x', 20000) };
        string sql = "SELECT id%1000,COUNT(CASE WHEN stamp(id)%3=0 THEN NULL ELSE @long END)"
            + (filtered ? " FILTER(WHERE id%2=0)" : "") + " FROM t GROUP BY id%1000";

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters);

        // Assert
        var rows = await Rows(result); rows.Length.ShouldBe(1000);
        for (int key = 0; key < 1000; key++)
        { rows[key].Cells[1].ShouldBe(Enumerable.Range(0, 8).LongCount(i => (key + i * 1000) % 3 != 0 && (!filtered || key % 2 == 0))); }
        log.Count.ShouldBe(filtered ? 4000 : 8000);
        SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash keys: invariant collation needs no index form, including lone surrogates (#1789)")]
    [InlineData("valid")]
    [InlineData("\ud800")]
    public async Task ExecuteAsync_HashKeyFamilies_ShouldAvoidSortCodec(string key)
    {
        // Arrange
        await using var reference = await Create("key-reference", 67108864);
        await using var engine = await Create("key-spill", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 8000); await Seed(b, 8000);
        var parameters = new Dictionary<string, object?> { ["key"] = key };
        const string sql = "SELECT id%1000,(@key COLLATE invariant),COUNT(*) FROM t GROUP BY id%1000,(@key COLLATE invariant)";

        // Act / Assert
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql, parameters);
        await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql, parameters);
        (await Rows(right)).ShouldBe(await Rows(left));
        SqlHashSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash differential: repeated groups preserve bitwise keys, ties, AVG, sequences and representatives (#1789)")]
    public async Task ExecuteAsync_HashStraddlingGroups_ShouldMatchMaterializedReference()
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        var original = SqlDatabaseEngine.CreateBuilder("straddle-reference");
        original.Options.StatementMemoryBudget = 67108864;
        original.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { logs[0].Add(value); return value; }));
        original.Functions.Add(SqlScalarFunction.Create<long, double>("float_key", FloatingKey));
        original.Functions.Add(SqlScalarFunction.Create<long, decimal>("decimal_key", DecimalKey));
        await using var reference = await original.BuildAsync();
        var builder = SqlDatabaseEngine.CreateBuilder("straddle-spill");
        builder.Options.TempDirectory = Path.Combine(_root, ".straddle"); builder.Options.StatementMemoryBudget = 262144;
        builder.Options.StatementTempSpaceQuota = 67108864;
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { logs[1].Add(value); return value; }));
        builder.Functions.Add(SqlScalarFunction.Create<long, double>("float_key", FloatingKey));
        builder.Functions.Add(SqlScalarFunction.Create<long, decimal>("decimal_key", DecimalKey));
        await using var engine = await builder.BuildAsync();
        var referenceDatabase = await reference.CreateDatabaseAsync("db"); referenceDatabase.Definitions.ForceSelectRetentionBasePath = true;
        await using var a = await referenceDatabase.CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 8000); await Seed(b, 8000);
        await a.ExecuteAsync("UPDATE t SET note=CASE WHEN id<1000 THEN 'a' ELSE 'A' END");
        await b.ExecuteAsync("UPDATE t SET note=CASE WHEN id<1000 THEN 'a' ELSE 'A' END");
        string[] queries =
        [
            "SELECT id%1000,COUNT(*),SUM(CAST(k AS DECIMAL(12,2))),AVG(CAST(k AS DECIMAL(12,2))),MIN(k),MAX(k) FROM t GROUP BY id%1000",
            "SELECT float_key(id),MIN(float_key(id)),MAX(float_key(id)),AVG(CAST(k AS DECIMAL(12,2))) FROM t GROUP BY float_key(id)",
            "SELECT decimal_key(id),MIN(decimal_key(id)),MAX(decimal_key(id)),AVG(CAST(k AS DECIMAL(12,2))) FROM t GROUP BY decimal_key(id)",
            "SELECT id%1000,note,COUNT(*) FROM t GROUP BY id%1000,note",
            "SELECT DISTINCT id%1000,note FROM t",
            "SELECT id%1000,COUNT(*) FILTER(WHERE stamp(id)%2=0),SUM(nextval('s')) FROM t GROUP BY id%1000",
        ];
        // Act / Assert
        foreach (string sql in queries)
        {
            logs[0].Clear(); logs[1].Clear();
            await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql);
            (await Rows(right)).ShouldBe(await Rows(left), customMessage: sql); logs[1].ShouldBe(logs[0], customMessage: sql);
            b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
            SqlHashSpillMetrics.For(b.LastStatementMetrics).ShouldNotBeNull(customMessage: sql);
        }
        await using var sa = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var sb = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(sb)).ShouldBe(await Rows(sa));

        static double FloatingKey(long id) => id % 1000 == 900 ? double.NaN : id % 1000 == 901 ? (id < 1000 ? -0.0 : 0.0) : id % 1000 + 1;
        static decimal DecimalKey(long id) => new((int)(id % 1000) * (id < 1000 ? 10 : 100), 0, 0, false, (byte)(id < 1000 ? 1 : 2));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash spool: unordered output never starts another external sort (#1789)")]
    public async Task ExecuteAsync_HashUnorderedOutput_ShouldSpoolInInputOrder()
    {
        // Arrange
        await using var engine = await Create("unordered-spool", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        var phases = new List<string>(); SqlRowCollector.ObserveSpill = phases.Add;
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id HAVING COUNT(*)>0");

            // Assert
            (await Rows(result)).Select(r => r.Cells[0]).ShouldBe(Enumerable.Range(0, 8000).Cast<object>());
            phases.ShouldContain("output-spool"); phases.ShouldNotContain("handoff");
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }
    [Fact(DisplayName = "Cohesion Test [Sql] - Hash damage: a malformed tuple with a valid CRC carries the statement location (#1789)")]
    public async Task ExecuteAsync_HashMalformedTuple_ShouldLocateDamage()
    {
        // Arrange
        await using var engine = await Create("hash-malformed", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await Seed(session, 8000); bool injected = false;
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected) { return; }
            foreach (var run in StatementRuns(engine.TempSpace!).Where(r => r.IsSealed && r.RecordCount > 0))
            {
                var page = new byte[8192]; RandomAccess.Read(Handle(run), page, 0);
                page[36] = 255; // The first tuple's codec version, after page and record framing.
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), 0);
                uint crc = uint.MaxValue;
                foreach (byte value in page)
                {
                    crc ^= value;
                    for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78u); }
                }
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(28), ~crc);
                RandomAccess.Write(Handle(run), page, 0); injected = true;
            }
        });
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        try
        {
            var statement = new SqlStatementContext(transaction, database.Coordinator, catalogSnapshot: database.Catalog.CaptureSnapshot());
            var request = SqlQueryRequest.FromSql("SELECT id,COUNT(*) FROM t GROUP BY id");
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions, default, statement)
                .Plan(request.Statement.SqlExpression, statement.Snapshot);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act: exercise the executor boundary, before the session can add a location.
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await executor.ExecuteRootAsync(plan, statement, CancellationToken.None, session, request.Statement.SqlExpression.Location));

            // Assert
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05004"); failure.Message.ShouldBe(ErrorMessages.SpillRowDamaged);
            failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash proof: derived unknown values and unchecked numeric result modifiers supply no bound (#1789)")]
    [InlineData(true, 262144)]
    [InlineData(true, 67108864)]
    [InlineData(false, 262144)]
    [InlineData(false, 67108864)]
    public async Task ExecuteAsync_HashDerivedUnprovedType_ShouldMatchOriginalPath(bool unknown, long budget)
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        var builders = new[] { SqlDatabaseEngine.CreateBuilder("derived-original"), SqlDatabaseEngine.CreateBuilder("derived-spill") };
        builders[1].Options.TempDirectory = Path.Combine(_root, ".derived");
        for (int index = 0; index < 2; index++)
        {
            var calls = logs[index]; builders[index].Options.StatementMemoryBudget = budget;
            builders[index].Options.StatementTempSpaceQuota = 67108864;
            builders[index].Functions.Add(new HashUnprovedResultFunction(unknown));
            builders[index].Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { calls.Add(value); return value; }));
        }
        await using var reference = await builders[0].BuildAsync(); await using var engine = await builders[1].BuildAsync();
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 800); await Seed(b, 800);
        string sql = unknown
            ? "SELECT stamp(id),SUM(v) FROM (SELECT id,unproved_result(id,NULL) AS v FROM t) q GROUP BY stamp(id)"
            : "SELECT stamp(id%300),SUM(v) FROM (SELECT id,unproved_result(id,NULL) AS v FROM t) q GROUP BY stamp(id%300)";
        if (unknown)
        {
            await using var sample = (DatabaseResultSet)await a.ExecuteAsync("SELECT v FROM (SELECT unproved_result(id,NULL) AS v FROM t WHERE id=500) q");
            sample.Columns[0].Type.ShouldBe(DatabaseType.Null); (await Rows(sample)).ShouldHaveSingleItem().Cells[0].ShouldBe(1e300);
        }

        // Act
        var left = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
        var right = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));

        // Assert
        right.Diagnostics[0].Code.ShouldBe(left.Diagnostics[0].Code); logs[1].ShouldBe(logs[0]);
        right.Diagnostics[0].Code.ShouldBe(budget == 67108864 ? "CS02002" : "CS05067");
        SqlHashSpillMetrics.For(b.LastStatementMetrics!).ShouldBeNull(); TempBytes(engine.TempSpace!).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash partition fault hook stays within its async test context (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashPartitionHook_ShouldNotAffectConcurrentStatement(bool distinct)
    {
        // Arrange: the hooked context parks after publishing its hook; the ordinary statement
        // then runs while that hook is live. Progress signals, never elapsed time, order the work.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = await Create("hook-isolation", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var a = await database.CreateSessionAsync(); await using var b = await database.CreateSessionAsync();
        await Seed(a, 20000);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string sql = distinct ? "SELECT DISTINCT id FROM t" : "SELECT id,COUNT(*) FROM t GROUP BY id";
        var hooked = Task.Run(async () =>
        {
            SqlPlanExecutor.HashSpillPartitionHash = _ => 0;
            try
            {
                ready.SetResult(); await release.Task.WaitAsync(ceiling.Token);
                var error = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql, cancellationToken: ceiling.Token));
                error.Message.ShouldContain("8-pass", Case.Sensitive);
            }
            finally { SqlPlanExecutor.HashSpillPartitionHash = null; }
        }, ceiling.Token);
        await ready.Task.WaitAsync(ceiling.Token);
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await b.ExecuteAsync(sql, cancellationToken: ceiling.Token);

            // Assert
            (await Rows(result, ceiling.Token)).Length.ShouldBe(20000);
        }
        finally { release.TrySetResult(); await hooked; SqlPlanExecutor.HashSpillPartitionHash = null; }
        TempBytes(engine.TempSpace!).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash output spool admits wide projections within its remaining grant (#1789)")]
    public async Task ExecuteAsync_HashWideProjection_ShouldUseOutputGrantBound()
    {
        // Arrange
        await using var engine = await Create("hash-wide-projection", 1048576);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, 8000);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT RPAD(CAST(id AS TEXT),20000,'x'),COUNT(*) FROM t GROUP BY id LIMIT 1");

        // Assert
        var row = (await Rows(result)).ShouldHaveSingleItem(); row.Cells[0].ShouldBe("0" + new string('x',19999)); row.Cells[1].ShouldBe(1L);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1048576);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash proof: concrete derived metadata does not enforce a runtime value family (#1789)")]
    [InlineData("SUM")]
    [InlineData("MAX")]
    public async Task ExecuteAsync_HashDerivedConcreteMetadata_ShouldRefuseUnprovedFamily(string aggregate)
    {
        // Arrange: COALESCE resolves BIGINT metadata, but a polymorphic NULL-input
        // function can still supply text. The spill proof must not trust that metadata.
        var logs = new[] { new List<long>(), new List<long>() };
        var builders = new[] { SqlDatabaseEngine.CreateBuilder("family-original"), SqlDatabaseEngine.CreateBuilder("family-spill") };
        builders[1].Options.TempDirectory = Path.Combine(_root, ".derived-family");
        for (int index = 0; index < 2; index++)
        {
            var calls = logs[index]; builders[index].Options.StatementMemoryBudget = 262144;
            builders[index].Options.StatementTempSpaceQuota = 67108864;
            builders[index].Functions.Add(new HashUnprovedResultFunction(true, text: true));
            builders[index].Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { calls.Add(value); return value; }));
        }
        await using var reference = await builders[0].BuildAsync(); await using var engine = await builders[1].BuildAsync();
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 800); await Seed(b, 800);
        const string input = "(SELECT id,COALESCE(unproved_result(id,NULL),0) AS v FROM t) q";
        await using (var sample = (DatabaseResultSet)await a.ExecuteAsync("SELECT v FROM " + input + " WHERE id=500"))
        { sample.Columns[0].Type.ShouldBe(DatabaseType.Int64); (await Rows(sample)).ShouldHaveSingleItem().Cells[0].ShouldBe(new string('x',100)); }
        string sql = "SELECT stamp(id)," + aggregate + "(v) FROM " + input + " GROUP BY stamp(id)";

        // Act
        var left = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
        var right = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));

        // Assert
        right.Diagnostics[0].Code.ShouldBe(left.Diagnostics[0].Code); right.Diagnostics[0].Code.ShouldBe("CS05067");
        logs[1].ShouldBe(logs[0]); SqlHashSpillMetrics.For(b.LastStatementMetrics!).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered hash pagination keeps zero-limit calls and empty-result temp lifetime (#1789)")]
    [InlineData(0L, 17L)]
    [InlineData(0L, long.MaxValue)]
    [InlineData(13L, 17L)]
    [InlineData(1L, long.MaxValue)]
    public async Task ExecuteAsync_HashUnorderedPagination_ShouldKeepCallsAndEmptyLifetime(long limit, long offset)
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        var builder = SqlDatabaseEngine.CreateBuilder("pagination-reference");
        builder.Options.StatementMemoryBudget = 67108864;
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { logs[0].Add(value); return value; }));
        await using var reference = await builder.BuildAsync();
        await using var engine = await Create("pagination-spill", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 8000); await Seed(b, 8000);
        string sql = $"SELECT stamp(id),nextval('s'),COUNT(*) FROM t GROUP BY id LIMIT {limit} OFFSET {offset}";

        // Act
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
        await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql);

        // Assert
        var expected = await Rows(left); (await Rows(right)).ShouldBe(expected); logs[1].ShouldBe(logs[0]);
        if (limit == 0)
        { right.ShouldBeOfType<SqlMaterializedResultSet>(); TempBytes(engine.TempSpace!).ShouldBe(0); }
        await using var sa = (DatabaseResultSet)await a.ExecuteAsync("SELECT nextval('s')");
        await using var sb = (DatabaseResultSet)await b.ExecuteAsync("SELECT nextval('s')");
        (await Rows(sb)).ShouldBe(await Rows(sa));
    }

}
