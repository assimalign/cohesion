using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeTests
{
    private static GraphType PersonType() => GraphType.Create([
        GraphNodeType.Create("Person", ["Entity"], [GraphPropertyType.Create("name", DatabaseType.String, true), GraphPropertyType.Create("age", DatabaseType.Int64)]),
        GraphNodeType.Create("Place")], [GraphEdgeType.Create("VISITS", "Person", "Place", [GraphPropertyType.Create("count", DatabaseType.Int64, true)])]);

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: immutable declarations copy inputs and refuse malformed shapes")]
    public void Create_MutableInputs_ShouldCopyAndValidateDeclarations()
    {
        // Arrange
        var labels = new[] { "Entity" };
        var properties = new[] { GraphPropertyType.Create("name", DatabaseType.String, true) };
        var node = GraphNodeType.Create("Person", labels, properties);
        var nodes = new[] { node };
        var type = GraphType.Create(nodes);

        // Act
        labels[0] = "Changed";
        properties[0] = GraphPropertyType.Create("other", DatabaseType.Int64);
        nodes[0] = GraphNodeType.Create("Other");

        // Assert
        type.NodeTypes.ShouldHaveSingleItem().ShouldBeSameAs(node);
        node.ImpliedLabels.ShouldBe(["Entity"]);
        node.Properties.ShouldHaveSingleItem().Name.ShouldBe("name");
        Should.Throw<ArgumentException>(() => GraphNodeType.Create("Person", ["Person"]));
        Should.Throw<ArgumentException>(() => GraphNodeType.Create("Person", ["X", "X"]));
        Should.Throw<ArgumentException>(() => GraphType.Create([node, node]));
        Should.Throw<ArgumentException>(() => GraphPropertyType.Create("p", DatabaseType.Guid));
        Should.Throw<ArgumentException>(() => GraphNodeType.Create("Person", properties: [null!]));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: empty add alter and selective-drop fragments preserve absence and existing declarations")]
    public async Task ChangeGraphTypeAsync_EmptyFragments_ShouldPreserveCurrentStateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);

        // Act / Assert
        await EmptyChanges();
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), CancellationToken.None);
        await EmptyChanges();
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem().IdentifyingLabel.ShouldBe("N");

        async Task EmptyChanges()
        {
            await schema.AddGraphTypeAsync(GraphType.Create(), CancellationToken.None);
            await schema.AlterGraphTypeAsync(GraphType.Create(), CancellationToken.None);
            await schema.DropGraphTypeAsync([], [], CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: set add alter selective drop and reopen preserve complete declarations")]
    public async Task SetGraphTypeAsync_ReopenAndMutations_ShouldRoundTripAsync()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = GraphDatabaseEngine.Create("types", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();

        // Act
        await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None);
        await session.DisposeAsync();
        await database.DisposeAsync();
        database = await engine.OpenDatabaseAsync("graph", CancellationToken.None);
        await using var reopened = await database.CreateSessionAsync(CancellationToken.None);
        schema = GraphSchema.Open(database, reopened);

        // Assert
        var type = (await schema.GetGraphTypeAsync(CancellationToken.None))!;
        type.NodeTypes.Select(item => item.IdentifyingLabel).ShouldBe(["Person", "Place"]);
        type.NodeTypes[0].ImpliedLabels.ShouldBe(["Entity"]);
        type.NodeTypes[0].Properties.Select(item => (item.Name, item.Type, item.Required)).ShouldBe([("age", DatabaseType.Int64, false), ("name", DatabaseType.String, true)]);
        var edge = type.EdgeTypes.ShouldHaveSingleItem();
        (edge.RelationshipType, edge.SourceNodeType, edge.TargetNodeType).ShouldBe(("VISITS", "Person", "Place"));
        edge.Properties.ShouldHaveSingleItem().Required.ShouldBeTrue();
        await schema.AddGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Extra")]), CancellationToken.None);
        await schema.AlterGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Extra", ["Entity"])]), CancellationToken.None);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes[0].ImpliedLabels.ShouldBe(["Entity"]);
        await schema.DropGraphTypeAsync(["Person", "Extra"], ["VISITS"], CancellationToken.None);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem().IdentifyingLabel.ShouldBe("Place");
        await schema.DropGraphTypeAsync(CancellationToken.None);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull().NodeTypes.ShouldBeEmpty();
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldContain(item => item.Name == "Person");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: open semantics enforce node rules through every write path")]
    [InlineData("INSERT (:Person {name:'A'})", "CG03012")]
    [InlineData("CREATE (:Person:Entity)", "CG03003")]
    [InlineData("INSERT (:Person:Entity {name:1})", "CG03003")]
    [InlineData("INSERT (:Person:Entity {name:'A',age:'bad'})", "CG03003")]
    [InlineData("MATCH (n:Person) SET n.name=1", "CG03003")]
    [InlineData("MATCH (n:Person) SET n.name=NULL", "CG03003")]
    [InlineData("MATCH (n:Person) SET n={}", "CG03003")]
    [InlineData("MATCH (n:Person) REMOVE n.name", "CG03003")]
    [InlineData("MATCH (n:Person) REMOVE n:Entity", "CG03012")]
    [InlineData("INSERT (n:Other) SET n:Person", "CG03012")]
    [InlineData("MERGE (:Person {name:'B'})", "CG03012")]
    [InlineData("MERGE (n:Person {name:'A'}) ON MATCH SET n.age='bad'", "CG03003")]
    public async Task Execute_NodeTypeViolation_ShouldRollbackAndRemainAvailableAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person:Entity {name:'A',extra:1})", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:Person) RETURN n.name")).ShouldHaveSingleItem()[0].ShouldBe("A");
        await session.ExecuteAsync("INSERT (a:Undeclared {unknown:TRUE})-[:UNDECLARED {other:1}]->(a)", cancellationToken: CancellationToken.None);
        // Edge enforcement is deliberately GQ-T2; its model already round-trips.
        await session.ExecuteAsync("INSERT (a)-[:VISITS]->(a)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (n:Person) DETACH DELETE n", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: typed writes and MERGE final state share label and scalar enforcement")]
    public async Task CreateNodeAsync_NodeType_ShouldValidateAndAllowFinalMergeStateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(PersonType(), CancellationToken.None);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(session, ["Person"], cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03012");
        await database.CreateNodeAsync(session, ["Person", "Entity"], new Dictionary<string, object?> { ["name"] = "A", ["age"] = 1L }, CancellationToken.None);
        await session.ExecuteAsync("MERGE (n:Person {name:'B'}) ON CREATE SET n:Entity, n.age=2", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (n:Person) REMOVE n:Person, n:Entity, n.name", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: validation streams only owner postings and preserves previous type on refusal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetGraphTypeAsync_ExistingMembers_ShouldValidateOnlyOwnerAsync(bool invalid)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Old")]), CancellationToken.None);
        for (int i = 0; i < 16; i++) { await database.CreateNodeAsync(session, ["Unrelated"], cancellationToken: CancellationToken.None); }
        for (int i = 0; i < 4; i++)
        { await database.CreateNodeAsync(session, invalid && i == 0 ? ["Person"] : ["Person", "Entity"], new Dictionary<string, object?> { ["name"] = "A" }, CancellationToken.None); }
        long decoded = database.Store.NodeRecordsDecoded;

        // Act
        if (invalid)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        }
        else { await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None); }

        // Assert
        (database.Store.NodeRecordsDecoded - decoded).ShouldBe(invalid ? 1 : 4);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldContain(item => item.IdentifyingLabel == (invalid ? "Old" : "Person"));
        await session.ExecuteAsync("INSERT (:Undeclared)", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: work and memory refusals leave the previous type and grants intact")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetGraphTypeAsync_BudgetExhaustion_ShouldPreserveTypeAsync(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new() { QueryWorkBudget = memory ? 100 : 6, QueryMemoryBudget = memory ? 4096 : 1048576 });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
        for (int i = 0; i < 4; i++) { await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull().NodeTypes.ShouldBeEmpty();
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: decoded member payloads consume a temporary memory grant")]
    public async Task SetGraphTypeAsync_WideMember_ShouldChargePayloadAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new() { QueryMemoryBudget = 12000 });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
        await database.CreateNodeAsync(session, ["N"], new Dictionary<string, object?> { ["p"] = new string('x', 6000) }, CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.SetGraphTypeAsync(GraphType.Create([
            GraphNodeType.Create("N", properties: [GraphPropertyType.Create("p", DatabaseType.String)])]), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull().NodeTypes.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: token discovery consumes work and memory before publication")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetGraphTypeAsync_DiscoveryBudget_ShouldRollbackPartialTokensAsync(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new() { QueryWorkBudget = memory ? 100 : 11,
            QueryMemoryBudget = memory ? 7000 : 1048576 });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
        var stages = new List<string>();
        database.Catalog.AfterGraphTypeMutation = stage => stages.Add(stage);

        // Act
        DatabaseException error;
        try
        {
            error = await Should.ThrowAsync<DatabaseException>(async () => await schema.SetGraphTypeAsync(GraphType.Create([
                GraphNodeType.Create("N", ["I1", "I2", "I3", "I4"], [GraphPropertyType.Create("p", DatabaseType.String)])]), CancellationToken.None));
        }
        finally { database.Catalog.AfterGraphTypeMutation = null; }

        // Assert: actual partial discovery precedes refusal, and none of it survives.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        stages.Count.ShouldBeGreaterThan(1);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull().NodeTypes.ShouldBeEmpty();
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: all mutations refuse explicit transactions with CG04025")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GraphTypeDdl_ExplicitTransaction_ShouldRefuseAsync(int operation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            switch (operation)
            {
                case 0: await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None); break;
                case 1: await schema.AddGraphTypeAsync(GraphType.Create(), CancellationToken.None); break;
                case 2: await schema.AlterGraphTypeAsync(GraphType.Create(), CancellationToken.None); break;
                case 3: await schema.DropGraphTypeAsync(CancellationToken.None); break;
                default: await schema.DropGraphTypeAsync(["Person"], ["VISITS"], CancellationToken.None); break;
            }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04025");
        await transaction.RollbackAsync(CancellationToken.None);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: stale Snapshot writers cannot bypass newly set rules")]
    public async Task CreateNodeAsync_StaleSnapshot_ShouldConflictAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var stale = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await stale.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await GraphSchema.Open(database, ddl).SetGraphTypeAsync(PersonType(), CancellationToken.None);
        (await GraphSchema.Open(database, stale).GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();

        // Act
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await database.CreateNodeAsync(stale, ["Person"], cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: invalid add alter drop and references preserve the prior type")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ChangeGraphTypeAsync_InvalidOperation_ShouldPreservePreviousAsync(int operation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            switch (operation)
            {
                case 0: await schema.AddGraphTypeAsync(PersonType(), CancellationToken.None); break;
                case 1: await schema.AlterGraphTypeAsync(GraphType.Create([GraphNodeType.Create("New")]), CancellationToken.None); break;
                case 2: await schema.DropGraphTypeAsync(["Person"], [], CancellationToken.None); break;
                default: await schema.SetGraphTypeAsync(GraphType.Create(edgeTypes: [GraphEdgeType.Create("T", "Absent", "Absent")]), CancellationToken.None); break;
            }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03011");
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.EdgeTypes.ShouldHaveSingleItem().RelationshipType.ShouldBe("VISITS");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: validation sees data committed while waiting for the writer lock")]
    public async Task SetGraphTypeAsync_QueuedBehindWriter_ShouldValidateLatestMembersAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var writer = await database.CreateSessionAsync(token);
        await using var ddl = await database.CreateSessionAsync(token);
        var schema = GraphSchema.Open(database, ddl);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await database.CreateNodeAsync(writer, ["Person"], cancellationToken: token);
        using var waits = GraphTypeReviewRegressionTests.RecordLockWaits();
        var queued = schema.SetGraphTypeAsync(PersonType(), token).AsTask();
        GraphTypeReviewRegressionTests.AssertWriterWait(waits);
        queued.IsCompleted.ShouldBeFalse();

        // Act
        await transaction.CommitAsync(token);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await queued);

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        (await schema.GetGraphTypeAsync(token)).ShouldBeNull();
        await database.CreateNodeAsync(ddl, ["Person"], cancellationToken: token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: dropping the type preserves independent named constraints")]
    public async Task DropGraphTypeAsync_IndependentConstraint_ShouldRetainRuleAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(PersonType(), CancellationToken.None);
        Guid label = (await schema.GetLabelsAsync(CancellationToken.None)).Single(item => item.Name == "Person").Id;
        await schema.CreateConstraintAsync(new(label, "name_required", "name"), CancellationToken.None);

        // Act
        await schema.DropGraphTypeAsync(CancellationToken.None);

        // Assert
        (await schema.FindConstraintAsync("name_required", CancellationToken.None)).ShouldNotBeNull();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(session, ["Person"], cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = 1L }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: oversized declarations refuse before token discovery")]
    public async Task SetGraphTypeAsync_OversizedElement_ShouldPreservePreviousAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.SetGraphTypeAsync(GraphType.Create([
            GraphNodeType.Create("Oversized", properties: [GraphPropertyType.Create(new string('p', 8192), DatabaseType.String)])]), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull().NodeTypes.ShouldBeEmpty();
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: missing drop-name diagnostics remain bounded without splitting a surrogate pair")]
    public async Task DropGraphTypeAsync_LongMissingName_ShouldBoundDiagnosticAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create(), CancellationToken.None);
        string name = new string('x', 255) + "😀" + new string('x', 100000);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropGraphTypeAsync([name], [], CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03011");
        error.Message.Length.ShouldBeLessThan(512);
        error.Message.ShouldNotContain("\ud83d", Case.Sensitive);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Graph types: cancellation during owner validation preserves the previous type")]
    public async Task SetGraphTypeAsync_CanceledDuringScan_ShouldPreservePreviousAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Old")]), CancellationToken.None);
        await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        int visited = 0;
        database.Store.OnLabelEntryRead = () => { visited++; canceled.Cancel(); };

        // Act
        try
        { await Should.ThrowAsync<OperationCanceledException>(async () => await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), canceled.Token)); }
        finally { database.Store.OnLabelEntryRead = null; }

        // Assert
        visited.ShouldBe(1);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem().IdentifyingLabel.ShouldBe("Old");
        await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: crash images at each replacement boundary recover the exact previous or committed type")]
    [InlineData("label:Person")]
    [InlineData("label:Entity")]
    [InlineData("label:Place")]
    [InlineData("property:age")]
    [InlineData("property:name")]
    [InlineData("property:count")]
    [InlineData("type:VISITS")]
    [InlineData("tombstones")]
    [InlineData("marker")]
    [InlineData("node:Person")]
    [InlineData("node:Place")]
    [InlineData("edge:VISITS")]
    [InlineData("committed")]
    [InlineData("commit-record")]
    [InlineData("committed-drop")]
    [InlineData("drop")]
    public async Task SetGraphTypeAsync_CrashAtBoundary_ShouldConvergeAsync(string boundary)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("types-crash", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        var schema = GraphSchema.Open(database, session);
        if (boundary != "marker") { await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Old")]), token); }
        int captured = 0;
        database.Catalog.AfterGraphTypeMutation = stage =>
        {
            if (stage != (boundary == "drop" ? "tombstones" : boundary)) { return; }
            Capture();
            throw new InvalidOperationException("Abandon graph type publication at " + stage);
        };

        // Act
        try
        {
            if (boundary == "committed") { await schema.SetGraphTypeAsync(PersonType(), token); Capture(); }
            else if (boundary == "commit-record")
            {
                FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => { strategy.CopyDatabase("source", "image"); captured++; };
                await schema.SetGraphTypeAsync(PersonType(), token);
            }
            else if (boundary == "committed-drop") { await schema.DropGraphTypeAsync(token); Capture(); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                {
                    if (boundary == "drop") { await schema.DropGraphTypeAsync(token); }
                    else { await schema.SetGraphTypeAsync(PersonType(), token); }
                });
                failure.InnerException.ShouldBeOfType<InvalidOperationException>();
            }
        }
        finally { database.Catalog.AfterGraphTypeMutation = null; FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }

        // Assert: recover before cleaning up the source, then reopen the recovered image again.
        captured.ShouldBe(1);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var image = await engine.OpenDatabaseAsync("image", token);
            await using var reader = await image.CreateSessionAsync(token);
            var recoveredSchema = GraphSchema.Open(image, reader);
            var recovered = await recoveredSchema.GetGraphTypeAsync(token);
            if (boundary is "committed-drop" or "marker") { recovered.ShouldBeNull(); }
            else
            {
                recovered.ShouldNotBeNull().NodeTypes.Select(item => item.IdentifyingLabel).ShouldBe(boundary is "committed" or "commit-record" ? ["Person", "Place"] : ["Old"]);
                recovered.EdgeTypes.Count.ShouldBe(boundary is "committed" or "commit-record" ? 1 : 0);
            }
            (await recoveredSchema.GetLabelsAsync(token)).Select(item => item.Name).ShouldBe(boundary is "committed" or "commit-record" ? ["Entity", "Old", "Person", "Place"] : boundary == "marker" ? [] : ["Old"]);
            (await recoveredSchema.GetRelationshipTypesAsync(token)).Select(item => item.Name).ShouldBe(boundary is "committed" or "commit-record" ? ["VISITS"] : []);
            var context = await image.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            try
            {
                foreach (string property in new[] { "age", "name", "count" })
                { image.Catalog.HasPropertyKey(property, context.Snapshot).ShouldBe(boundary is "committed" or "commit-record", property); }
            }
            finally { await image.Coordinator.RollbackAsync(context, token); }
            await reader.DisposeAsync();
            await image.DisposeAsync();
        }

        void Capture()
        {
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            strategy.CopyDatabase("source", "image");
            captured++;
        }
    }
}
