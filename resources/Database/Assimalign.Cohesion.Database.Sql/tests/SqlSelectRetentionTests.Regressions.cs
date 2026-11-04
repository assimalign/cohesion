using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectRetentionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: DISTINCT hash failures preserve every base projection and predicate call (#1776)")]
    public async Task ExecuteAsync_DistinctHashFailure_ShouldCompleteBaseEvaluation()
    {
        // Arrange: a fixed collation rejects the invalid scalar only when it hashes the result.
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("retention-distinct-fault");
        builder.Functions.Add(SqlScalarFunction.Create("bad_text", (long value) =>
        { calls.Add(value); return value == 1 ? "\ud800" : "valid"; }));
        builder.Functions.Add(SqlScalarFunction.Create("record_call", (long value) => { calls.Add(value); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE SEQUENCE s");
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE u(k INT)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(3)");
        await Execute(session, "INSERT INTO u VALUES(1)");
        var parameters = new Dictionary<string, object?> { ["bad"] = "\ud800" };
        foreach (var shape in new[]
        {
            ("SELECT DISTINCT bad_text(k),nextval('s') FROM t LIMIT 1", 4L),
            ("SELECT DISTINCT bad_text(k),nextval('s') FROM t LIMIT 0", 4L),
            ("SELECT DISTINCT @bad FROM t a CROSS JOIN u b WHERE record_call(a.k)>0", 1L),
        })
        {
            foreach (bool baseline in new[] { true, false })
            {
                database.Definitions.ForceSelectRetentionBasePath = baseline;
                await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");
                calls.Clear();

                // Act
                await Should.ThrowAsync<DatabaseTypeException>(async () =>
                    await session.ExecuteAsync(shape.Item1, parameters, CancellationToken.None));

                // Assert: hashing follows every projection at base, including LIMIT 0.
                calls.ShouldBe([1L, 2L, 3L], shape.Item1);
                (await Rows(session, "SELECT nextval('s')")).ShouldBe([[shape.Item2]]);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: wide grouped write sources keep the DML path and exact effects (#1776)")]
    [InlineData("UPDATE t SET total=g.s FROM (SELECT doc,SUM(v) AS s FROM u GROUP BY doc) g WHERE t.doc=g.doc", 5)]
    [InlineData("DELETE FROM t USING (SELECT doc,SUM(v) AS s FROM u GROUP BY doc) g WHERE t.doc=g.doc", -1)]
    [InlineData("MERGE INTO t USING (SELECT doc,SUM(v) AS s FROM u GROUP BY doc) g ON t.doc=g.doc WHEN MATCHED THEN UPDATE SET total=g.s", 5)]
    public async Task ExecuteAsync_WideGroupedDml_ShouldKeepExpectedEffects(string sql, int expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("retention-writes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string key = new('q', 200);
        await Execute(session, "CREATE TABLE t(doc TEXT,total BIGINT)");
        await Execute(session, "CREATE TABLE u(doc TEXT,v INT)");
        await Execute(session, $"INSERT INTO t VALUES('{key}',0),('other',7)");
        await Execute(session, $"INSERT INTO u VALUES('{key}',2),('{key}',3)");
        var paths = new List<(string Name, bool Pipeline)>();
        database.Definitions.ObserveSelectRetention = (name, pipeline, _) => paths.Add((name, pipeline));

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        paths.ShouldContain(("group-input", false));
        paths.ShouldNotContain(item => item.Pipeline);
        var rows = await Rows(session, "SELECT doc,total FROM t ORDER BY total");
        if (expected < 0) { rows.ShouldBe([["other", 7L]]); }
        else { rows.ShouldBe([[key, (long)expected], ["other", 7L]]); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: prior evaluation regressions retain hand-computed results and calls (#1776)")]
    public async Task ExecuteAsync_EvaluationRegressions_ShouldKeepExpectedValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("retention-evaluation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE SEQUENCE s");
        await Execute(session, "CREATE TABLE t(k INT,x BIGINT)");
        await Execute(session, "CREATE TABLE u(v INT)");
        await Execute(session, "INSERT INTO t VALUES(1,0),(2,0),(3,0)");
        await Execute(session, "INSERT INTO u VALUES(1)");

        // Act / Assert: an independent derived table is executed once throughout correlated DML.
        var refusal = await Should.ThrowAsync<DatabaseParseException>(() => Execute(session,
            "UPDATE t SET x=(SELECT MAX(d.n) FROM (SELECT nextval('s') AS n FROM u) d WHERE t.k>0)"));
        refusal.Diagnostics.ShouldContain(item => item.Code == "CK00001");
        (await Rows(session, "SELECT (SELECT MAX(d.n) FROM (SELECT nextval('s') AS n FROM u) d WHERE t.k>0) FROM t ORDER BY k"))
            .ShouldBe([[1L], [1L], [1L]]);
        (await Rows(session, "SELECT nextval('s')")).ShouldBe([[2L]]);
        await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");
        (await Rows(session, "SELECT DISTINCT nextval('s') FROM t LIMIT 1")).ShouldBe([[1L]]);
        (await Rows(session, "SELECT nextval('s')")).ShouldBe([[4L]]);
        await Should.ThrowAsync<DatabaseException>(() => Rows(session, "SELECT DISTINCT 10/(k-2) FROM t LIMIT 1"));
        await Execute(session, "CREATE TABLE duplicates(k INT,c1 INT)");
        await Execute(session, "INSERT INTO duplicates VALUES(1,5),(1,0),(2,3)");
        (await Rows(session, "SELECT DISTINCT k FROM duplicates ORDER BY c1 LIMIT 1")).ShouldBe([[1]]);
        await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");
        foreach (bool baseline in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = baseline;
            await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");
            (await Rows(session, "SELECT nextval('s') FROM t GROUP BY k ORDER BY (SELECT v FROM u WHERE t.k>0) LIMIT 0")).ShouldBeEmpty();
            // At cc605c78d independent sequence outputs are deferred past the LIMIT 0 check.
            (await Rows(session, "SELECT nextval('s')")).ShouldBe([[1L]]);
        }
        database.Definitions.ForceSelectRetentionBasePath = false;
        await Execute(session, "CREATE TABLE a(id INT PRIMARY KEY,note TEXT)");
        await Execute(session, "CREATE TABLE b(aid INT)");
        await Execute(session, "INSERT INTO a VALUES(1,'one'),(2,'two')");
        await Execute(session, "INSERT INTO b VALUES(2),(3),(1)");
        (await Rows(session, "SELECT a.note FROM a RIGHT JOIN b ON a.id=b.aid")).ShouldBe([["two"], [null], ["one"]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: binary keys and MIN/MAX detach cells across storage pages (#1776)")]
    public async Task ExecuteAsync_BinaryAcrossPages_ShouldKeepExpectedExtrema()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("retention-binary", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k BINARY,v BINARY)");
        var keys = new[] { new byte[] { 0, 1 }, new byte[] { 0, 2 } };
        var low = new byte[512];
        var high = Enumerable.Repeat((byte)255, 512).ToArray();
        for (int i = 0; i < 128; i++)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES(@key,@value)", new Dictionary<string, object?>
            { ["key"] = keys[i % 2], ["value"] = i < 2 ? low : high }, CancellationToken.None);
        }

        // Act / Assert
        foreach (bool baseline in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = baseline;
            var groups = await Rows(session, "SELECT k,MIN(v),MAX(v),COUNT(*) FROM t GROUP BY k ORDER BY k");
            groups.Length.ShouldBe(2);
            for (int i = 0; i < groups.Length; i++)
            {
                ((byte[])groups[i][0]!).ShouldBe(keys[i]);
                ((byte[])groups[i][1]!).ShouldBe(low);
                ((byte[])groups[i][2]!).ShouldBe(high);
                groups[i][3].ShouldBe(64L);
            }
            var distinct = await Rows(session, "SELECT DISTINCT k FROM t");
            distinct.Length.ShouldBe(2);
            ((byte[])distinct[0][0]!).ShouldBe(keys[0]);
            ((byte[])distinct[1][0]!).ShouldBe(keys[1]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: 262144-row inputs retain constant intermediate rows and do the same work (#1776)")]
    public async Task ExecuteAsync_LargeInput_ShouldBoundIntermediateRetention()
    {
        // Arrange: doubling uses the statement snapshot and avoids a giant VALUES parse.
        const int count = 262144;
        int calls = 0;
        long sampledLive = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("retention-size");
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
        builder.Options.PageWriteBackInterval = TimeSpan.FromHours(1);
        builder.Functions.Add(SqlScalarFunction.Create("sample_memory", (long value) =>
        {
            if (++calls == count - 8) { sampledLive = GC.GetTotalMemory(forceFullCollection: true); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE one(k INT)");
        await Execute(session, "INSERT INTO one VALUES(1)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(3),(4)");
        for (int size = 4; size < count; size *= 2) { await Execute(session, "INSERT INTO t SELECT k FROM t"); }
        var observations = new Dictionary<string, int>();
        database.Definitions.ObserveSelectRetention = (name, _, retained) =>
            observations[name] = Math.Max(observations.GetValueOrDefault(name), retained);
        foreach (var shape in new[]
        {
            ("COUNT", "SELECT COUNT(*) FROM t WHERE sample_memory(k)>0", "group-input", 1),
            ("GROUP", "SELECT k,COUNT(*) FROM t WHERE sample_memory(k)>0 GROUP BY k", "group-input", 1),
            ("DISTINCT", "SELECT DISTINCT k FROM t WHERE sample_memory(k)>0", "distinct", 4),
            ("EXISTS", "SELECT EXISTS(SELECT sample_memory(k) FROM t)", "subquery-body", 2),
            ("scalar aggregate", "SELECT (SELECT SUM(k) FROM t WHERE sample_memory(k)>0)", "group-input", 1),
            ("join LIMIT", "SELECT t.k FROM t CROSS JOIN one WHERE sample_memory(t.k)>0 LIMIT 10", "join-matches", 1),
        })
        {
            // Act
            database.Definitions.ForceSelectRetentionBasePath = true;
            observations.Clear();
            calls = 0;
            long startingLive = GC.GetTotalMemory(forceFullCollection: true);
            var baseline = await Rows(session, shape.Item2);
            calls.ShouldBe(count);
            long beforeLive = sampledLive - startingLive;
            int before = observations[shape.Item3];
            long reads = session.LastStatementMetrics!.RecordsExamined;
            long pairs = session.LastStatementMetrics.JoinCandidatePairs;
            database.Definitions.ForceSelectRetentionBasePath = false;
            observations.Clear();
            calls = 0;
            startingLive = GC.GetTotalMemory(forceFullCollection: true);
            var actual = await Rows(session, shape.Item2);
            calls.ShouldBe(count);
            long afterLive = sampledLive - startingLive;

            // Assert: constant intermediate retention, with identical rows and complete work.
            actual.ShouldBe(baseline);
            before.ShouldBe(count);
            observations[shape.Item3].ShouldBe(shape.Item4);
            // Independent forced-GC evidence detects a hidden list even if its observation is wrong.
            beforeLive.ShouldBeGreaterThan(8_000_000);
            afterLive.ShouldBeLessThan(2_000_000);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(reads);
            session.LastStatementMetrics.JoinCandidatePairs.ShouldBe(pairs);
            _output.WriteLine($"{shape.Item1}: retained input/results {before} -> {observations[shape.Item3]}; live-byte delta {beforeLive} -> {afterLive}; reads={reads}; pairs={pairs}");
        }
    }
}
