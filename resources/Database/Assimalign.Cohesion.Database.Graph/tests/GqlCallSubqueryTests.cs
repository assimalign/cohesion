using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlCallSubqueryTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - CALL invocation results, imports and boundaries preserve cardinality")]
    [InlineData("CALL { RETURN 1 AS y } RETURN y", "1")]
    [InlineData("FOR x IN [1,2] CALL () { FOR z IN [3,4] RETURN z AS y } RETURN x,y", "1|3;1|4;2|3;2|4")]
    [InlineData("FOR x IN [1,2] CALL (x) { RETURN x+1 AS y } RETURN x,y", "1|2;2|3")]
    [InlineData("LET x=1,z=2 CALL (*) { RETURN x+z AS y } RETURN y", "3")]
    [InlineData("LET x=1 CALL { WITH x RETURN x+1 AS y } RETURN y", "2")]
    [InlineData("LET x=1 CALL { WITH * RETURN x+1 AS y } RETURN y", "2")]
    [InlineData("LET x=1 CALL { WITH * RETURN x AS y UNION WITH x RETURN x+1 AS y } RETURN y ORDER BY y", "1;2")]
    [InlineData("LET x=1 CALL { WITH x RETURN x AS y UNION WITH x RETURN x+1 AS y } RETURN y ORDER BY y", "1;2")]
    [InlineData("LET x=5 CALL (x) { WITH 1 AS z RETURN x+z AS y } RETURN y", "6")]
    [InlineData("LET x=5 CALL (x) { FILTER FALSE RETURN count(*)+x AS y ORDER BY x } RETURN y", "5")]
    [InlineData("LET x=5 CALL (x) { FOR z IN [1,2] WITH count(*)+x AS n ORDER BY x WHERE x=5 RETURN n AS y } RETURN y", "7")]
    [InlineData("CALL () { RETURN {a:[1,2]} AS y } RETURN y.a[1]", "2")]
    [InlineData("CALL () { FILTER FALSE RETURN 1 AS y OTHERWISE RETURN 2 AS y } RETURN y", "2")]
    [InlineData("LET x=5 CALL (x) { WITH DISTINCT 1 AS z WHERE x=5 RETURN x+z AS y } RETURN y", "6")]
    [InlineData("LET x=5 CALL (x) { WITH 1 AS z ORDER BY z WHERE x=5 RETURN x+z AS y } RETURN y", "6")]
    [InlineData("LET x=5 CALL (x) { FOR z IN [1,1] WITH count(*) AS n RETURN x+n AS y } RETURN y", "7")]
    [InlineData("LET x=5 CALL (x) { RETURN 1 AS z NEXT RETURN x+z AS y } RETURN y", "6")]
    [InlineData("LET x=5 CALL (x) { (RETURN x UNION RETURN x) NEXT RETURN x+1 AS y } RETURN y", "6")]
    [InlineData("LET x=5 CALL (x) { (RETURN 1 AS z UNION RETURN 2 AS z) NEXT RETURN x+z AS y } RETURN y ORDER BY y", "6;7")]
    [InlineData("CALL () { (RETURN 1 AS y UNION RETURN 2 AS y) INTERSECT RETURN 2 AS y } RETURN y", "2")]
    [InlineData("FOR x IN [1,2] CALL (x) { FILTER x=1 RETURN x+1 AS y } RETURN x,y", "1|2")]
    [InlineData("FOR x IN [1,2] OPTIONAL CALL (x) { FILTER x=1 RETURN x+1 AS y } RETURN x,y", "1|2;2|NULL")]
    [InlineData("FOR x IN [] OPTIONAL CALL () { RETURN 1 AS y } RETURN y", "")]
    [InlineData("LET x=NULL CALL (x) { RETURN x AS y } RETURN y", "NULL")]
    [InlineData("CALL { FILTER FALSE RETURN count(*) AS y } RETURN y", "0")]
    [InlineData("CALL { LET z=1 CALL (z) { RETURN z+1 AS y } RETURN y } RETURN y", "2")]
    [InlineData("CALL { RETURN 1 AS y } WITH y AS z CALL (z) { RETURN z+1 AS w } RETURN w", "2")]
    [InlineData("INSERT (:N {k:3}) CALL () { MATCH (n:N) RETURN n.k AS y } RETURN y", "3")]
    public async Task Execute_CallRows_ShouldApplyPerInput(string source, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var actual = await Rows(session, source);
        // Assert
        actual.ShouldBe(expected);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - CALL scope admission validates every body even on empty input")]
    [InlineData("LET x=1 CALL () { RETURN x AS y } RETURN y", "CG05001")]
    [InlineData("LET x=1 CALL { RETURN x AS y } RETURN y", "CG05001")]
    [InlineData("LET x=1 CALL (missing) { RETURN 1 AS y } RETURN y", "CG05001")]
    [InlineData("LET x=1 CALL (x,x) { RETURN 1 AS y } RETURN y", "CG03003")]
    [InlineData("LET x=1 CALL (x) { LET x=2 RETURN x AS y } RETURN y", "CG03003")]
    [InlineData("LET x=1 CALL (x) { WITH 2 AS x RETURN x AS y } RETURN y", "CG03003")]
    [InlineData("LET x=1 CALL (x) { RETURN x } RETURN x", "CG01024")]
    [InlineData("LET x=1 CALL (*) { RETURN * } RETURN x", "CG01024")]
    [InlineData("LET \"x y\"=1 CALL (\"x y\") { RETURN \"x y\" } RETURN 1", "CG01024")]
    [InlineData("LET x=1 CALL () { RETURN 2 AS x } RETURN x", "CG01024")]
    [InlineData("LET x=1 CALL () { RETURN 2 AS x UNION RETURN 3 AS x } RETURN x", "CG01024")]
    [InlineData("CALL () { LET z=1 RETURN z AS y } RETURN z", "CG05001")]
    [InlineData("MATCH (n) DELETE n CALL () { RETURN 1 AS n } WITH 2 AS z RETURN n", "CG05001")]
    [InlineData("CALL () { RETURN 1+1 } RETURN *", "CG05001")]
    [InlineData("CALL () { LET x=1 RETURN x,(x) } RETURN x", "CG01015")]
    [InlineData("CALL { MATCH (n) } RETURN 1", "CK00001")]
    [InlineData("CALL { WITH 1 AS y } RETURN 1", "CK00001")]
    [InlineData("CALL {} RETURN 1", "CK00001")]
    [InlineData("CALL {}", "CK00001")]
    [InlineData("CALL { MATCH (n) }", "CK00001")]
    [InlineData("CALL () { RETURN 1 AS y }", "CG05001")]
    [InlineData("CALL { INSERT (:N) RETURN 1 AS y } RETURN y", "CK00001")]
    [InlineData("CALL { CREATE (:N) RETURN 1 AS y } RETURN y", "CK00001")]
    [InlineData("CALL { MERGE (:N) RETURN 1 AS y } RETURN y", "CK00001")]
    [InlineData("CALL { MATCH (n) SET n.k=1 RETURN 1 AS y } RETURN y", "CK00001")]
    [InlineData("CALL { MATCH (n) REMOVE n.k RETURN 1 AS y } RETURN y", "CK00001")]
    [InlineData("CALL { MATCH (n) DELETE n } RETURN 1", "CK00001")]
    [InlineData("CALL { CALL { INSERT (:N) RETURN 1 AS z } RETURN z AS y } RETURN y", "CK00001")]
    [InlineData("CALL { RETURN 1 AS y UNION INSERT (:N) RETURN 2 AS y } RETURN y", "CK00001")]
    [InlineData("LET x=1 CALL { WITH x AS y RETURN y } RETURN y", "CK00001")]
    [InlineData("LET x=1 CALL { WITH x WHERE x=1 RETURN x AS y } RETURN y", "CK00001")]
    [InlineData("LET x=1 CALL { WITH DISTINCT x RETURN x AS y } RETURN y", "CK00001")]
    [InlineData("LET x=1 CALL { WITH x RETURN x AS y UNION RETURN 2 AS y } RETURN y", "CK00001")]
    public async Task Execute_InvalidCall_ShouldRefuseBeforeReading(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "FOR empty IN [] ", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source, cancellationToken: CancellationToken.None));
            error.Diagnostics.Single(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Code.ShouldBe(code);
            await Rows(session, "RETURN 7");
        }
        (await Rows(session, "MATCH (n) RETURN count(*)")).ShouldBe("0");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - generated CALL oracle agrees with LET, FOR and OPTIONAL MATCH rewrites")]
    public async Task Execute_GeneratedCalls_ShouldMatchRewrites()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-oracle", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,1,3,5] INSERT (:N {k:k})", cancellationToken: CancellationToken.None);
        var random = new Random(1807);
        // Act / Assert
        for (int sample = 0; sample < 32; sample++)
        {
            string values = string.Join(',', Enumerable.Range(0, random.Next(0, 9)).Select(_ => random.Next(0, 7)));
            int offset = random.Next(-3, 4);
            string start = "FOR x IN [" + values + "] ";
            string projected = " RETURN x,y ORDER BY x,y";
            (await Rows(session, start + "CALL (x) { RETURN x+" + offset + " AS y }" + projected))
                .ShouldBe(await Rows(session, start + "LET y=x+" + offset + projected));
            (await Rows(session, start + "CALL (x) { FOR z IN [x,x+1] RETURN z AS y }" + projected))
                .ShouldBe(await Rows(session, start + "FOR y IN [x,x+1]" + projected));
            (await Rows(session, start + "OPTIONAL CALL (x) { MATCH (n:N {k:x}) RETURN n.k AS y }" + projected))
                .ShouldBe(await Rows(session, start + "OPTIONAL MATCH (n:N {k:x}) LET y=n.k" + projected));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - CALL shares statement limits and unwinds nested memory on refusal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CallBudget_ShouldReleaseInnerState(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("FOR x IN [1,2] CALL (x) { FOR z IN $xs RETURN z+x AS y ORDER BY y } RETURN count(*)",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 1000).ToArray() },
            memory ? new() { MemoryBytes = 8192 } : new() { WorkUnits = 20 });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBufferCapacity.ShouldBe(0);
        (await Rows(session, "RETURN 7")).ShouldBe("7");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - cancellation during inner CALL scan releases arguments, sort state and cursors")]
    public async Task Execute_CanceledCall_ShouldReleaseInnerState()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-cancel", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR x IN $xs INSERT (:N {k:x})", new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 30).ToArray() }, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        int visits = 0;
        database.Store.OnLabelEntryRead = () => { if (++visits == 10) { canceled.Cancel(); } };
        // Act / Assert
        try
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "LET x=1 CALL (x) { MATCH (n:N) RETURN n.k+x AS y ORDER BY y } RETURN y", cancellationToken: canceled.Token));
            error.CancellationToken.ShouldBe(canceled.Token);
        }
        finally { database.Store.OnLabelEntryRead = null; }
        visits.ShouldBe(10);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBufferCapacity.ShouldBe(0);
        (await Rows(session, "RETURN 7")).ShouldBe("7");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXPLAIN exposes Apply and OptionalApply with their nested plans")]
    public async Task Explain_Call_ShouldDescribeApplyAndCompositeBody()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-explain", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var description = await Rows(session, "EXPLAIN CALL () { RETURN 1 AS x UNION RETURN 2 AS x } OPTIONAL CALL (x) { FILTER FALSE RETURN x AS y } RETURN y");
        // Assert
        description.ShouldContain("Apply", Case.Sensitive); description.ShouldContain("OptionalApply", Case.Sensitive);
        description.ShouldContain("Union", Case.Sensitive); description.ShouldContain("shared_snapshot=True", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - CALL shares the enclosing snapshot and function registries")]
    public async Task Execute_SnapshotAndFunctions_ShouldStayInOuterOperation()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("call-snapshot");
        int calls = 0;
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("observe", value => { calls++; return value+1; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(Assimalign.Cohesion.Database.Transactions.IsolationLevel.Snapshot, CancellationToken.None);
        (await Rows(session, "MATCH (n:N) RETURN count(*)")).ShouldBe("1");
        await using var peer = await database.CreateSessionAsync(CancellationToken.None);
        await peer.ExecuteAsync("INSERT (:N {k:2})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Rows(session, "FOR x IN [1,2] CALL (x) { MATCH (n:N) RETURN count(*)+x AS y } RETURN observe(y)"))
            .ShouldBe("3;4");
        calls.ShouldBe(2);
        (await Rows(session, "CALL () { MATCH (n:N) RETURN observe(n.k) AS y } RETURN observe(y)" )).ShouldBe("3");
        calls.ShouldBe(4);
        (await Rows(session, "CALL () { RETURN EXISTS { MATCH (n:N {k:1}) } AS y } RETURN y")).ShouldBe("True");
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - CALL early exit closes an uncorrelated invocation before advancing input")]
    public async Task Execute_CallLimit_ShouldCloseInnerAndEvaluatePerRow()
    {
        // Arrange
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("call-limit");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await Rows(session, "FOR x IN [1,2,3] CALL () { RETURN observe(7) AS y } RETURN y")).ShouldBe("7;7;7");
        calls.ShouldBe(3);
        (await Rows(session, "FOR x IN [1,2,3] CALL () { FOR z IN [1,2,3] RETURN observe(z) AS y } RETURN y LIMIT 1")).ShouldBe("1");
        calls.ShouldBe(4);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - OPTIONAL CALL preserves declared result types and nullable path admission")]
    public async Task Execute_OptionalCall_ShouldPreserveMetadataAndRejectNullablePaths()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("OPTIONAL CALL () { FILTER FALSE RETURN 1 AS y } RETURN y", cancellationToken: CancellationToken.None))
        {
            result.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
            result.Columns[0].IsNullable.ShouldBeTrue();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBeNull(); }
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql("OPTIONAL CALL () { MATCH (n) RETURN n AS entity } RETURN entity"), CancellationToken.None));
        error.Diagnostics.Single(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Code.ShouldBe("CG05001");
    }

    private static async Task<string> Rows(GraphDatabaseSession session, string source)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(string.Join('|', Enumerable.Range(0, row.FieldCount).Select(i => row.GetValue(i) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : "NULL"))); }
        return string.Join(';', rows);
    }
}
