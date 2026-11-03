using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeReviewRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - graph type changes skip unchanged and relaxed owner postings (#1806)")]
    [InlineData("add")]
    [InlineData("relax")]
    [InlineData("drop")]
    [InlineData("edge")]
    [InlineData("same")]
    public async Task ChangeGraphTypeAsync_UnaffectedMembers_ShouldAvoidScanAsync(string change)
    {
        // Arrange: each owner grows beyond a statement's work budget after publication.
        await using var engine = GraphDatabaseEngine.Create("types", new() { QueryWorkBudget = 32 });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        GraphType Original() => GraphType.Create([
            GraphNodeType.Create("N", ["Implied"], [GraphPropertyType.Create("p", DatabaseType.Int64, true)]),
            GraphNodeType.Create("Other")], [GraphEdgeType.Create("R", "N", "Other")]);
        await schema.SetGraphTypeAsync(Original(), CancellationToken.None);
        for (int i = 0; i < 48; i++)
        {
            await database.CreateNodeAsync(session, ["N", "Implied"], new Dictionary<string, object?> { ["p"] = 1L }, CancellationToken.None);
            await database.CreateNodeAsync(session, ["Other"], cancellationToken: CancellationToken.None);
        }
        long decoded = database.Store.NodeRecordsDecoded;
        var stages = new List<string>();
        database.Catalog.AfterGraphTypeMutation = stages.Add;

        // Act
        try
        {
            switch (change)
            {
                case "add": await schema.AddGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Empty")]), CancellationToken.None); break;
                case "relax": await schema.AlterGraphTypeAsync(GraphType.Create([
                    GraphNodeType.Create("N", properties: [GraphPropertyType.Create("p", DatabaseType.Int64)])]), CancellationToken.None); break;
                case "drop": await schema.DropGraphTypeAsync(["Other"], ["R"], CancellationToken.None); break;
                case "edge": await schema.AlterGraphTypeAsync(GraphType.Create(edgeTypes: [GraphEdgeType.Create("R", "N", "N")]), CancellationToken.None); break;
                default: await schema.SetGraphTypeAsync(Original(), CancellationToken.None); break;
            }
        }
        finally { database.Catalog.AfterGraphTypeMutation = null; }

        // Assert: no member or unrelated node was decoded and unchanged records stayed put.
        database.Store.NodeRecordsDecoded.ShouldBe(decoded);
        stages.ShouldNotContain("marker");
        stages.ShouldNotContain("node:Other");
        if (change != "relax") { stages.ShouldNotContain("node:N"); }
        if (change is "add" or "same" or "relax") { stages.ShouldNotContain("edge:R"); }
        if (change == "same") { stages.ShouldNotContain("tombstones"); }
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Snapshot writes conflict only for their added changed or removed node rule (#1806)")]
    [InlineData("add", true)]
    [InlineData("change", true)]
    [InlineData("remove", true)]
    [InlineData("unchanged", false)]
    public async Task CreateNodeAsync_TypePublication_ShouldCheckSelectedRuleAsync(string change, bool conflict)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, ddl);
        if (change != "add") { await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), CancellationToken.None); }
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        switch (change)
        {
            case "add": await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), CancellationToken.None); break;
            case "change": await schema.AlterGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", properties: [GraphPropertyType.Create("p", DatabaseType.Int64)])]), CancellationToken.None); break;
            case "remove": await schema.DropGraphTypeAsync(CancellationToken.None); break;
            default: await schema.AddGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Other")]), CancellationToken.None); break;
        }

        // Act / Assert
        if (conflict)
        {
            var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
                await database.CreateNodeAsync(writer, ["N"], cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        else
        {
            await database.CreateNodeAsync(writer, ["N"], cancellationToken: CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - full type DROP charges each unpurged version once (#1806)")]
    public async Task DropGraphTypeAsync_UnpurgedVersions_ShouldUseOneDecodeGrantAsync()
    {
        // Arrange: pin the first versions until a second declaration of every element commits.
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = GraphDatabaseEngine.Create("types", new() { StorageStrategy = strategy,
            MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        string name = new('p', 100);
        GraphType Type(bool required) => GraphType.Create([
            GraphNodeType.Create("N", properties: [GraphPropertyType.Create(name, DatabaseType.Int64, required)])],
            [GraphEdgeType.Create("R", "N", "N", [GraphPropertyType.Create(name, DatabaseType.Int64, required)])]);
        await schema.SetGraphTypeAsync(Type(false), CancellationToken.None);
        await using var pin = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await schema.SetGraphTypeAsync(Type(true), CancellationToken.None);
        // Both versions of kinds 7/8 are physically retained; count the single load before DROP.
        long onePass = 0;
        database.Catalog.GetGraphType(pin.Context.Snapshot, bytes => onePass += bytes).ShouldNotBeNull();
        onePass.ShouldBeGreaterThan(10_000);
        onePass.ShouldBeLessThan(30_000 - 512);

        // Act: exactly the one-load grant, including route and candidate capacity.
        engine.EngineOptions.QueryMemoryBudget = onePass + 512;
        await schema.DropGraphTypeAsync(CancellationToken.None);

        // Assert
        session.LastStatementMetrics!.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(onePass + 512);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();
        await pin.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - identifying and implied labels cannot overlap in a complete type (#1806)")]
    [InlineData("set")]
    [InlineData("add")]
    [InlineData("alter")]
    public async Task ChangeGraphTypeAsync_IdentifyingImpliedOverlap_ShouldRefuseAsync(string verb)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("A"), GraphNodeType.Create("B")]), CancellationToken.None);
        var fragment = GraphType.Create([GraphNodeType.Create(verb == "add" ? "C" : "A", ["B"])]);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (verb == "set") { await schema.SetGraphTypeAsync(GraphType.Create([fragment.NodeTypes[0], GraphNodeType.Create("B")]), CancellationToken.None); }
            else if (verb == "add") { await schema.AddGraphTypeAsync(fragment, CancellationToken.None); }
            else { await schema.AlterGraphTypeAsync(fragment, CancellationToken.None); }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03011");
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.Select(node => node.IdentifyingLabel).ShouldBe(["A", "B"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - typed and text token DROP refuse graph type dependencies (#1806)")]
    [InlineData("N", false, false)]
    [InlineData("I", false, false)]
    [InlineData("R", true, false)]
    [InlineData("N", false, true)]
    [InlineData("I", false, true)]
    [InlineData("R", true, true)]
    public async Task DropDefinitionAsync_TypeDependency_ShouldRefuseBothSurfacesAsync(string name, bool relationship, bool text)
    {
        // Arrange: no entity uses these tokens.
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", ["I"])], [GraphEdgeType.Create("R", "N", "N")]), CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (text) { await session.ExecuteAsync("DROP " + (relationship ? "RELATIONSHIP TYPE " : "LABEL ") + name, cancellationToken: CancellationToken.None); }
            else if (relationship) { await schema.DropRelationshipTypeAsync(name, CancellationToken.None); }
            else { await schema.DropLabelAsync(name, CancellationToken.None); }
        });

        // Assert: the existing in-use refusal, tokens and type all survive.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        (await schema.GetLabelsAsync(CancellationToken.None)).Select(label => label.Name).ShouldBe(["I", "N"]);
        (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("R");
        await schema.DropGraphTypeAsync(CancellationToken.None);
        if (relationship) { await schema.DropRelationshipTypeAsync(name, CancellationToken.None); }
        else { await schema.DropLabelAsync(name, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - graph types honor ownership of identifying implied and relationship definitions (#1806)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SetGraphTypeAsync_SchemaOwnedDefinition_ShouldRefuseAsync(int kind)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var id = Guid.NewGuid();
        if (kind == 2) { await schema.SaveRelationshipTypeAsync(new(id, "Owned", DatabaseObjectOwner.Schema, "Application"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "Owned", DatabaseObjectOwner.Schema, "Application"), CancellationToken.None); }
        var type = GraphType.Create([GraphNodeType.Create(kind == 0 ? "Owned" : "N", kind == 1 ? ["Owned"] : [])],
            kind == 2 ? [GraphEdgeType.Create("Owned", "N", "N")] : []);

        // Act
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.SetGraphTypeAsync(type, CancellationToken.None));

        // Assert
        error.ObjectName.ShouldBe("Owned");
        (await schema.GetGraphTypeAsync(CancellationToken.None)).ShouldBeNull();
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldNotContain(label => label.Name == "N");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - graph type and independent property types reject conflicts in either order (#1806)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SetGraphTypeAsync_ConflictingPropertyRule_ShouldRefuseBothOrdersAsync(bool typeFirst, bool declaredKey)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        var type = GraphType.Create([GraphNodeType.Create("N", properties: [GraphPropertyType.Create("p", DatabaseType.Int64)])]);
        async Task Rule()
        {
            if (declaredKey) { await schema.SavePropertyKeyAsync(new(id, "p", DatabaseType.String), CancellationToken.None); }
            else { await schema.CreateConstraintAsync(new(id, "p_type", "p", DatabaseType.String), CancellationToken.None); }
        }
        if (typeFirst) { await schema.SetGraphTypeAsync(type, CancellationToken.None); }
        else { await Rule(); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (typeFirst) { await Rule(); }
            else { await schema.SetGraphTypeAsync(type, CancellationToken.None); }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
        await database.CreateNodeAsync(session, ["N"], new Dictionary<string, object?> { ["p"] = typeFirst ? 1L : "ok" }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - selective DROP identifies the public collection argument (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropGraphTypeAsync_DuplicateName_ShouldReportPublicParameterAsync(bool edge)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);

        // Act
        var error = Should.Throw<ArgumentException>(() => schema.DropGraphTypeAsync(edge ? [] : ["N", "N"], edge ? ["R", "R"] : [], CancellationToken.None));

        // Assert
        error.ParamName.ShouldBe(edge ? "edgeTypes" : "nodeTypes");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - typed label write pins one decoded declaration for snapshot and latest (#1806)")]
    public async Task GetNodeTypeForWrite_TypedLabel_ShouldDecodeCandidateOnceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N")]), CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        int reads = 0;
        database.Catalog.BeforeRecordRead = () => reads++;

        // Act
        string? selectedLabel;
        try { selectedLabel = database.Catalog.GetNodeTypeForWrite("N", transaction.Context.Snapshot, database.LatestSnapshot(transaction.Context))?.IdentifyingLabel; }
        finally { database.Catalog.BeforeRecordRead = null; }

        // Assert
        reads.ShouldBe(1);
        selectedLabel.ShouldBe("N");
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - queued ReadCommitted writer refreshes after type DDL commits (#1806)")]
    public async Task CreateNodeAsync_QueuedBehindDdl_ShouldValidateRefreshedTypeAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var ddl = await database.CreateSessionAsync(token);
        await using var writer = await database.CreateSessionAsync(token);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        database.Catalog.AfterGraphTypeMutation = stage =>
        {
            if (stage != "node:N") { return; }
            entered.Set();
            release.Wait(token);
        };
        using var waits = RecordLockWaits();
        var ddlTask = Task.Run(async () => await GraphSchema.Open(database, ddl).SetGraphTypeAsync(GraphType.Create([
            GraphNodeType.Create("N", properties: [GraphPropertyType.Create("p", DatabaseType.Int64, true)])]), token), token);
        Task<Protocol.GraphNode>? queued = null;
        try
        {
            entered.Wait(token);
            queued = database.CreateNodeAsync(writer, ["N"], cancellationToken: token).AsTask();
            AssertWriterWait(waits);
            queued.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }

        // Act
        try
        {
            await ddlTask;
            var error = await Should.ThrowAsync<DatabaseException>(async () => await queued!);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
        finally { database.Catalog.AfterGraphTypeMutation = null; }

        // Assert: the refreshed rule was used rather than a stale-snapshot CG04007 or a bypass.
        await transaction.RollbackAsync(token);
        await database.CreateNodeAsync(writer, ["N"], new Dictionary<string, object?> { ["p"] = 1L }, token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - selective DROP names consume statement work (#1806)")]
    public async Task DropGraphTypeAsync_ManyNames_ShouldChargeWorkAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var names = Enumerable.Range(0, 30).Select(i => "N" + i).ToArray();
        await schema.SetGraphTypeAsync(GraphType.Create(names.Select(name => GraphNodeType.Create(name))), CancellationToken.None);
        engine.EngineOptions.QueryWorkBudget = 32;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropGraphTypeAsync(names.Take(15).ToArray(), [], CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.Count.ShouldBe(30);
    }

    internal static EventSourceRecorder RecordLockWaits()
        => new(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);

    internal static void AssertWriterWait(EventSourceRecorder recorder)
        => recorder.Events.ShouldContain(item => item.EventName == "LockWaitStart" && item.Payload != null
            && Equals(item.Payload[2], "Database") && Equals(item.Payload[3], "Exclusive"));
}
