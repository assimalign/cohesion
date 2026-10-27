using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphMergeExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE matches the complete pattern or creates every unbound part (#1735)")]
    [InlineData("->")]
    [InlineData("-")]
    [InlineData("<-")]
    public async Task Execute_PartialPattern_ShouldCreateWholeUnboundPath(string direction)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {id:1}), (:P {id:2})");
        string edge = direction == "<-" ? "<-[r:R]-" : "-[r:R]" + direction;
        string query = $"MERGE (a:P {{id:1}}){edge}(b:P {{id:2}}) ON CREATE SET r.hits=1 ON MATCH SET r.hits=r.hits+1 RETURN r.hits";

        // Act
        var first = await session.ExecuteAsync(query);
        var second = await session.ExecuteAsync(query);

        // Assert
        first.AffectedCount.ShouldBe(3);
        (await Read(first)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        second.AffectedCount.ShouldBe(1);
        (await Read(second)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await Rows(session, "MATCH (n:P) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(4L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE reuses bound endpoints and earlier creations in repeated rows (#1735)")]
    public async Task Execute_RepeatedInputs_ShouldObserveOverlayAndReuseBoundNodes()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {id:1}), (:P {id:2})");

        // Act
        var result = await session.ExecuteAsync("MATCH (a:P {id:1}), (b:P {id:2}) FOR x IN [1,1,1] MERGE (a)-[r:R {id:x}]-(b) ON CREATE SET r.hits=1 ON MATCH SET r.hits=r.hits+1 RETURN r.hits");
        var ownRead = await Rows(session, "FOR x IN [4,4] MERGE (n:Overlay {id:x}) WITH n MATCH (m:Overlay {id:4}) RETURN m.id");

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await Read(result)).ShouldAllBe(row => Equals(row[0], 3L));
        ownRead.Count.ShouldBe(2);
        (await Rows(session, "MATCH (n:P) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE applies ON MATCH to every complete match (#1735)")]
    public async Task Execute_MultipleMatches_ShouldUpdateEveryMatchWithoutCreating()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {id:1}), (:P {id:1})");

        // Act
        var result = await session.ExecuteAsync("MERGE (n:P {id:1}) ON CREATE SET n.bad=1 ON MATCH SET n.hit=2 RETURN n.hit");

        // Assert
        result.AffectedCount.ShouldBe(2);
        (await Read(result)).Select(row => row[0]).ShouldBe(new object?[] { 2L, 2L });
        (await Rows(session, "MATCH (n:P) RETURN n.bad")).ShouldAllBe(row => row[0] == null);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE reuses bound nodes and bare repeated node variables (#1735)")]
    public async Task Execute_BoundNodesAndLoops_ShouldReuseIdentities()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:P {id:1})-[r:R]->(b:P {id:2})");

        // Act
        var reused = await session.ExecuteAsync("MATCH (a)-[:R]->(b) MERGE (a)-[:R]->(b)-[:S]->(c:P {id:3}) RETURN a.id, b.id, c.id");
        var loop = await session.ExecuteAsync("MERGE (n:Loop {id:1})-[:SELF]->(n) RETURN n.id");

        // Assert
        reused.AffectedCount.ShouldBe(3);
        (await Read(reused)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L, 3L });
        loop.AffectedCount.ShouldBe(2);
        (await Rows(session, "MATCH (:Loop)-[:SELF]->(:Loop) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE validates both SET branches and property equalities under EXPLAIN (#1735)")]
    [InlineData("MERGE (n:P {id:NULL})", "CG03004")]
    [InlineData("MERGE (n:P {id:n.id})", "CG05001")]
    [InlineData("MERGE (n:P) ON MATCH SET missing.x=1", "CG05001")]
    [InlineData("MERGE ()-[r:R]->() ON CREATE SET r:Label", "CG03003")]
    [InlineData("MERGE (n:A|B)", "CG05001")]
    [InlineData("MERGE ()-[]-()", "CG05001")]
    [InlineData("MERGE (a)-[r:R]->(b)-[r:R]->(c)", "CG05001")]
    public async Task Execute_InvalidMerge_ShouldFailBeforeMutation(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();

        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source));
            failure.Diagnostics[0].Code.ShouldBe(code);
            failure.Message.ShouldNotContain(code, Case.Sensitive);
        }
        (await Rows(session, "MATCH (n) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE null bound targets and failing branches abort all statement writes (#1735)")]
    [InlineData("INSERT (:Before) OPTIONAL MATCH (n:Missing) MERGE (n)-[:R]->(:P)", "CG03005")]
    [InlineData("LET key=NULL MERGE ()-[:Absent {id:key}]->()", "CG03004")]
    [InlineData("INSERT (:Before) MERGE (n:P) ON CREATE SET n.x=1/0", "CG02013")]
    [InlineData("INSERT (:Before) LET m={k:NULL} MERGE (n:P {id:m.k})", "CG03004")]
    public async Task Execute_RuntimeFailure_ShouldRollBackEveryCreation(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source));

        // Assert
        failure.Diagnostics[0].Code.ShouldBe(code);
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();
        (await Rows(session, "MATCH (n) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXPLAIN exposes MERGE probes and both branches without writing (#1735)")]
    public async Task Execute_ExplainMerge_ShouldDescribeWithoutMutating()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();

        // Act
        var rows = await Rows(session, "EXPLAIN MERGE (n:P {id:1}) ON CREATE SET n.x=2 ON MATCH SET n.x=3 RETURN n.x");

        // Assert
        rows.Select(row => row[2]).ShouldContain("Merge");
        rows.Select(row => row[2]).ShouldContain("OnCreate");
        rows.Select(row => row[2]).ShouldContain("OnMatch");
        (await Rows(session, "MATCH (n) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE anonymous freshness bindings cannot collide with delimited user variables (#1735)")]
    public async Task Execute_InternalNameCollision_ShouldPreserveUserBinding()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();

        // Act
        var result = await session.ExecuteAsync("MERGE (\"$merge0\":P {id:7})-[:R]->() RETURN \"$merge0\".id");

        // Assert
        result.AffectedCount.ShouldBe(3);
        (await Read(result)).ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE exhausted statement budgets release every creation and lease (#1735)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_ExceededBudget_ShouldAbortWithoutPublishing(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var limits = memory ? new GraphQueryLimits { MemoryBytes = 8000 } : new GraphQueryLimits { WorkUnits = 5 };

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphQueryRequest.FromGql("FOR x IN [1,2,3,4,5,6,7,8] MERGE (n:P {id:x}) RETURN n.id", parameters: null, limits: limits)));

        // Assert
        error.Diagnostics[0].Code.ShouldBe(memory ? "CG05020" : "CG05004");
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        await transaction.RollbackAsync();
        (await Rows(session, "MATCH (n) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - bound undirected MERGE relationships are refused before writes (#1735)")]
    public async Task Execute_BoundUndirectedChain_ShouldRefuseRedeclarations()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:P {id:1})<-[:R]-(b:P {id:2})-[:S]->(c:P {id:3})");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH ()-[r:R]->(), ()-[s:S]->() MERGE (x)-[r]-(y)-[s]-(z)-[:T]->(:P {id:4}) RETURN x.id,y.id,z.id"));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CG03007");
        (await Rows(session, "MATCH (n:P) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Snapshot MERGE evaluates equality operands once per input before both probes (#1735)")]
    public async Task Execute_SnapshotProbes_ShouldReuseEvaluatedEqualityValues()
    {
        // Arrange
        int evaluations = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("merge");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("merge_key", value =>
        { Interlocked.Increment(ref evaluations); return value; }));
        await using var engine = await builder.BuildAsync();
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE (:P {id:0})");
        await GraphSchema.Open(db, session).CreateIndexAsync("P", "by_id", "id");
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot);

        // Act
        var result = await session.ExecuteAsync("FOR x IN [7,7] MERGE (n:P {id:merge_key(x)}) RETURN n.id");

        // Assert
        evaluations.ShouldBe(2);
        result.AffectedCount.ShouldBe(1);
        (await Read(result)).Select(row => row[0]).ShouldBe(new object?[] { 7L, 7L });
        await transaction.RollbackAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE later rows refresh earlier nested entity aliases (#1735)")]
    public async Task Execute_RepeatedMergeRows_ShouldRefreshEveryCompositeAlias()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {id:1,hits:0})");

        // Act
        var result = await session.ExecuteAsync("MATCH (a:P) LET nested={v:[a]} FOR x IN [1,2] MERGE (n:P {id:1}) ON MATCH SET n.hits=n.hits+1 RETURN nested");

        // Assert
        result.AffectedCount.ShouldBe(1);
        var rows = await Read(result);
        rows.Count.ShouldBe(2);
        foreach (var row in rows)
        {
            var map = row[0].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
            map.ShouldNotBeNull();
            map["v"].ShouldBeOfType<object?[]>()[0].ShouldBeOfType<GraphNode>().Properties["hits"].ShouldBe(2L);
        }
    }

    internal static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string source, CancellationToken token = default)
        => await Read(await session.ExecuteAsync(source, cancellationToken: token));

    internal static async Task<List<object?[]>> Read(DatabaseResult result)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return values;
    }
}
