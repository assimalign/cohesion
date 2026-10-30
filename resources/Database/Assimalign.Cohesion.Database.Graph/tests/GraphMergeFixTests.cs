using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeFixTests
{
    private readonly ITestOutputHelper _output;

    public GraphMergeFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE refuses redeclarations during planning and EXPLAIN (#1735)")]
    [InlineData("MATCH ()-[r:R]->() MERGE (a)-[r]->(b)-[:S]->(:P {id:3})", "CG03007")]
    [InlineData("MERGE (n:N {v:1})-[:R]->(n {v:1.0}) RETURN n.v", "CG03006")]
    [InlineData("MERGE (n:N)-[:R]->(n:Other)", "CG03006")]
    [InlineData("MATCH (n:P) MERGE (n:P)-[:S]->()", "CG03006")]
    [InlineData("MATCH (n:P) MERGE (n {id:1})-[:S]->()", "CG03006")]
    public async Task Execute_RedeclaredMergeEntity_ShouldRefuseBeforeAnyWrite(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE (:P {id:1})-[:R]->(:P {id:2})");

        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source));
            failure.Diagnostics[0].Code.ShouldBe(code);
        }
        (await GraphMergeExecutionTests.Rows(session, "MATCH (n) RETURN count(*)")).Single()[0].ShouldBe(2L);
        (await GraphMergeExecutionTests.Rows(session, "MATCH ()-[r]->() RETURN count(*)")).Single()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE ON CREATE supplies Required properties before validation (#1735)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_RequiredOnCreateProperty_ShouldValidateFinishedEntity(bool relationship, bool explicitTransaction)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        var schema = GraphSchema.Open(db, session);
        var definition = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(definition, "Person")); }
        else { await schema.SaveLabelAsync(new(definition, "Person")); }
        await schema.SavePropertyKeyAsync(new(definition, "name", DatabaseType.String, true));
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync() : null;
        string pattern = relationship ? "()-[n:Person {id:1}]->()" : "(n:Person {id:1})";

        // Act
        var result = await session.ExecuteAsync("MERGE " + pattern + " ON CREATE SET n.name='x' RETURN n.name");

        // Assert
        result.AffectedCount.ShouldBe(relationship ? 3 : 1);
        (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe("x");
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Active); await transaction.CommitAsync(); }
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MERGE " + pattern.Replace("id:1", "id:2", StringComparison.Ordinal)));
        missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        missing.Message.ShouldNotStartWith("CG03003:", Case.Sensitive);
        string match = relationship ? "MATCH ()-[n:Person]->()" : "MATCH (n:Person)";
        (await GraphMergeExecutionTests.Rows(session, match + " RETURN count(*)")).Single()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - repeated MERGE branches execute each SET item in source order (#1735)")]
    public async Task Execute_RepeatedBranches_ShouldAppendAssignmentsInOrder()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        const string source = "MERGE (n:P) ON CREATE SET n.x=1 ON MATCH SET n.x=n.x+10 ON CREATE SET n.x=n.x+2 ON MATCH SET n.x=n.x*2 RETURN n.x";

        // Act
        var first = await GraphMergeExecutionTests.Rows(session, source);
        var second = await GraphMergeExecutionTests.Rows(session, source);

        // Assert
        first.Single()[0].ShouldBe(3L);
        second.Single()[0].ShouldBe(26L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE non-scalar equalities retain their dedicated code (#1735)")]
    [InlineData("UNWIND [[1]] AS x MERGE (:N {v:x})")]
    [InlineData("LET x = {k:1} MERGE (:N {v:x})")]
    [InlineData("UNWIND $rows AS x MERGE (:N {v:x})")]
    public async Task Execute_CompositeMergeEquality_ShouldReportMergeCode(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        var parameters = new Dictionary<string, object?> { ["rows"] = new object?[] { new Dictionary<string, object?> { ["k"] = 1 } } };

        // Act / Assert: all three have provable kinds, including the captured parameter shape.
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source, parameters));
            error.Diagnostics[0].Code.ShouldBe("CG03004");
        }
        var ordinary = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source.Replace("MERGE", "CREATE", StringComparison.Ordinal), parameters));
        ordinary.Diagnostics[0].Code.ShouldBe("CG03003");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - evaluated MERGE equalities use their dedicated runtime code (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UnknownCompositeMergeEquality_ShouldUseRuntimeMergeCode(bool map)
    {
        // Arrange: enter the executor with an evaluated row to exercise validation independently
        // of the planner's conservative shape proofs.
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns");
        await using var session = await db.CreateSessionAsync();
        var stage = (GqlMergeStage)GraphQueryRequest.FromGql("MERGE (:N {v:x})").Statement.GqlExpression.Stages.Single();
        var plan = new GraphPathPlan(stage.Path, new(0, "N", null, null)) { Shape = GraphPathShape.Create(stage.Path) };
        object value = map ? new Dictionary<string, object?> { ["k"] = 1L } : new object?[] { 1L };
        var budget = new GraphPlanExecutor.ExpansionBudget();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(db.RunAsync(session, async operation =>
        {
            operation.QueryBudget = budget;
            await foreach (var row in GraphPlanExecutor.StreamMergeAsync(db, operation, stage, plan,
                [new Dictionary<string, object?> { ["x"] = value }], budget, null, new StrongBox<long>(), CancellationToken.None))
            { _ = row; }
            return true;
        }, CancellationToken.None).AsTask());

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CG03004");
        budget.Memory.ActiveLeases.ShouldBe(0);
        (await GraphMergeExecutionTests.Rows(session, "MATCH (n) RETURN count(*)")).Single()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - row-dependent indexed MERGE upserts 10000 rows among 100000 within budget (#1735)")]
    public async Task Execute_IndexedRowEqualities_ShouldSeekEachEvaluatedValue()
    {
        // Arrange: build the fixture through the real transactional store, outside query accounting.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("fix", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(cancellationToken: token);
        var schema = GraphSchema.Open(db, session);
        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Person"), token);
        await db.RunAsync(session, async operation =>
        {
            await db.LockWriterAsync(operation, token);
            var labels = new[] { "Person" };
            var ids = db.NodeLabelIds(labels, operation);
            for (int i = 0; i < 100_000; i++)
            {
                await db.Store.CreateNodeAsync(new StoredGraphNode((ulong)db.DataStorage.ReserveTransactionSequence(), labels,
                    new Dictionary<string, object?> { ["id"] = (long)i }), ids, operation.Context, token);
            }
            return true;
        }, token);
        await schema.CreateIndexAsync("Person", "by_id", "id", token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var parameters = new Dictionary<string, object?> { ["rows"] = Enumerable.Range(0, 10_000).Select(i => new Dictionary<string, object?> { ["id"] = (long)(i * 10) }).ToArray() };

        // Act
        try
        {
            var result = await db.RunAsync(session, operation =>
            {
                operation.QueryBudget = budget;
                return GraphPlanExecutor.ExecuteAsync(db, operation,
                    GraphQueryRequest.FromGql("UNWIND $rows AS r MERGE (n:Person {id:r.id}) RETURN count(*)").Statement, parameters, token);
            }, token);

            // Assert
            result.AffectedCount.ShouldBe(0);
            (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(10_000L);
            budget.Consumed.ShouldBeLessThanOrEqualTo(200_000);
            await transaction.CommitAsync(token);
        }
        finally { _output.WriteLine($"indexed upsert: rows=10000; nodes=100000; work={budget.Consumed}; limit={budget.WorkLimit}"); }
        budget.Memory.ActiveLeases.ShouldBe(0);
    }
}
