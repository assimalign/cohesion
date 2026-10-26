using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphShortestPathTests
{
    private readonly ITestOutputHelper _output;

    public GraphShortestPathTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest path: unequal branches, cycles, loops, parallel edges and disconnected nodes (#1536)")]
    public async Task FindShortestPathAsync_UnequalBranches_ShouldReturnActualDistanceAndEntities()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("shortest", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        var c = await NodeAsync(database, session, "c");
        var d = await NodeAsync(database, session, "d");
        var target = await NodeAsync(database, session, "target");
        var isolated = await NodeAsync(database, session, "isolated");
        await EdgeAsync(database, session, a, b);
        await EdgeAsync(database, session, b, c);
        await EdgeAsync(database, session, c, d);
        await EdgeAsync(database, session, d, target);
        var shortcut = await database.CreateRelationshipAsync(session, b.Id, target.Id, "KNOWS",
            new Dictionary<string, object?> { ["weight"] = 99L });
        await EdgeAsync(database, session, b, target);
        await EdgeAsync(database, session, target, b);
        await EdgeAsync(database, session, a, a);
        await EdgeAsync(database, session, a, target, "OTHER");
        var search = new GraphShortestPathSearch(a.Id, target.Id, GraphDirection.Both, "KNOWS");

        // Act
        var result = await database.FindShortestPathAsync(session, search);

        // Assert
        result.ShouldNotBeNull().Distance.ShouldBe(2);
        result.Path.Nodes.Select(node => node.Id).ShouldBe([a.Id, b.Id, target.Id]);
        result.Path.Nodes[2].Properties["name"].ShouldBe("target");
        result.Path.Relationships[1].Id.ShouldBe(shortcut.Id);
        result.Path.Relationships[1].Type.ShouldBe("KNOWS");
        result.Path.Relationships[1].Properties["weight"].ShouldBe(99L); // Distance remains unweighted.
        await CheckDistanceAsync(database, session, search, 2);
        await CheckDistanceAsync(database, session, search with { Target = isolated.Id }, null);
        await CheckDistanceAsync(database, session, search with { Start = isolated.Id }, null);
        await CheckDistanceAsync(database, session, search with { RelationshipType = "knows" }, null);
        await CheckDistanceAsync(database, session, search with { RelationshipType = "Missing" }, null);
        await CheckDistanceAsync(database, session, search with { RelationshipType = "" }, null);
        await CheckDistanceAsync(database, session, search with { RelationshipType = " " }, null);
        await CheckDistanceAsync(database, session, search with { RelationshipType = null }, 1);
        await CheckDistanceAsync(database, session, search with { Start = a.Id, Target = a.Id, RelationshipType = "Missing" }, 0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: directions preserve stored endpoints (#1536)")]
    [InlineData(GraphDirection.Outgoing, false, 2)]
    [InlineData(GraphDirection.Outgoing, true, null)]
    [InlineData(GraphDirection.Incoming, false, null)]
    [InlineData(GraphDirection.Incoming, true, 2)]
    [InlineData(GraphDirection.Both, false, 2)]
    [InlineData(GraphDirection.Both, true, 2)]
    public async Task FindShortestPathAsync_DirectionFilter_ShouldPreserveTraversalOrder(GraphDirection direction, bool reverse, int? distance)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("directions", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        var c = await NodeAsync(database, session, "c");
        var ab = await EdgeAsync(database, session, a, b);
        var bc = await EdgeAsync(database, session, b, c);
        var search = new GraphShortestPathSearch(reverse ? c.Id : a.Id, reverse ? a.Id : c.Id, direction, "KNOWS");

        // Act / Assert
        await CheckDistanceAsync(database, session, search, distance);
        if (distance is null) { return; }
        var path = (await database.FindShortestPathAsync(session, search))!.Path;
        path.Nodes.Select(node => node.Id).ShouldBe(reverse ? [c.Id, b.Id, a.Id] : new[] { a.Id, b.Id, c.Id });
        path.Relationships.Select(edge => (edge.Id, edge.From, edge.To, edge.Type)).ShouldBe(
            (reverse ? new[] { bc, ab } : [ab, bc]).Select(edge => (edge.Id, edge.From, edge.To, edge.Type)));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: ties use the complete relationship identity sequence across reopen (#1536)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindShortestPathAsync_EqualLengths_ShouldChooseLexicographicRelationshipIds(bool filter)
    {
        // Arrange: node ID order and final-edge ID order both oppose first-edge ID order.
        await using var engine = GraphDatabaseEngine.Create("ties", new());
        var database = await engine.CreateDatabaseAsync("graph");
        GraphNode a, low, high, target;
        GraphRelationship first, last;
        await using (var session = await database.CreateSessionAsync())
        {
            a = await NodeAsync(database, session, "a");
            low = await NodeAsync(database, session, "low");
            high = await NodeAsync(database, session, "high");
            target = await NodeAsync(database, session, "target");
            first = await EdgeAsync(database, session, a, high, "T");
            await EdgeAsync(database, session, a, low, filter ? "T" : "U");
            await EdgeAsync(database, session, low, target, filter ? "T" : "V");
            last = await EdgeAsync(database, session, high, target, "T");
            await EdgeAsync(database, session, high, target, "T");
            var selected = await database.FindShortestPathAsync(session, new(a.Id, target.Id, RelationshipType: filter ? "T" : null));
            selected!.Path.Relationships.Select(edge => edge.Id).ShouldBe([first.Id, last.Id]);
        }

        // Act
        await database.DisposeAsync();
        database = await engine.OpenDatabaseAsync("graph");
        await using var reopened = await database.CreateSessionAsync();
        var result = await database.FindShortestPathAsync(reopened, new(a.Id, target.Id, RelationshipType: filter ? "T" : null));

        // Assert
        result.ShouldNotBeNull().Path.Nodes.Select(node => node.Id).ShouldBe([a.Id, high.Id, target.Id]);
        result.Path.Relationships.Select(edge => edge.Id).ShouldBe([first.Id, last.Id]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: absent endpoints never fabricate zero-length paths (#1536)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindShortestDistanceAsync_InvisibleEndpoint_ShouldReturnNull(bool delete)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("absence", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var missing = default(GraphNodeId);
        if (delete) { missing = a.Id; await database.DeleteNodeAsync(session, a.Id); }

        // Act / Assert
        await CheckDistanceAsync(database, session, new(missing, missing), null);
        await CheckDistanceAsync(database, session, new(a.Id, missing), null);
        await CheckDistanceAsync(database, session, new(missing, a.Id), null);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest path: searches beyond GQL's 64-hop bound (#1536)")]
    public async Task FindShortestPathAsync_LongChain_ShouldReturnEntirePath()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("long-chain", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var nodes = new List<GraphNode>();
        for (int i = 0; i <= 80; i++) { nodes.Add(await NodeAsync(database, session, i.ToString())); }
        for (int i = 0; i < 80; i++) { await EdgeAsync(database, session, nodes[i], nodes[i + 1]); }

        // Act
        var result = await database.FindShortestPathAsync(session, new(nodes[0].Id, nodes[80].Id), timeout.Token);

        // Assert
        result.ShouldNotBeNull().Distance.ShouldBe(80);
        result.Path.Nodes.Select(node => node.Id).ShouldBe(nodes.Select(node => node.Id));
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: snapshot and ReadCommitted searches see own writes and rollback (#1536)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task FindShortestDistanceAsync_TransactionVisibility_ShouldRespectIsolationAndOwnWrites(IsolationLevel isolation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("visibility", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        var a = await NodeAsync(database, writer, "a");
        var b = await NodeAsync(database, writer, "b");
        await using var transaction = await reader.BeginTransactionAsync(isolation);
        await EdgeAsync(database, writer, a, b);

        // Act / Assert
        await CheckDistanceAsync(database, reader, new(a.Id, b.Id), isolation == IsolationLevel.Snapshot ? null : 1);
        await transaction.RollbackAsync();
        await using var own = await writer.BeginTransactionAsync(isolation);
        var c = await NodeAsync(database, writer, "c");
        await EdgeAsync(database, writer, b, c);
        await CheckDistanceAsync(database, writer, new(a.Id, c.Id), 2);
        await CheckDistanceAsync(database, reader, new(a.Id, c.Id), null);
        await own.RollbackAsync();
        await CheckDistanceAsync(database, reader, new(a.Id, c.Id), null);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: invalid or exhausted budgets fail without partial output and release memory (#1536)")]
    [InlineData(true, 1, "CG05004")]
    [InlineData(true, 0, "CG08021")]
    [InlineData(true, 1000001, "CG08021")]
    [InlineData(false, 1, "CG05020")]
    [InlineData(false, 9700, "CG05020")]
    [InlineData(false, 10000, "CG05020")]
    [InlineData(false, 0, "CG08021")]
    [InlineData(false, 268435457, "CG08021")]
    public async Task FindShortestPathAsync_RefusedBudget_ShouldAbortExplicitTransaction(bool work, long limit, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("budgets", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        await EdgeAsync(database, session, a, b);
        await using var transaction = await session.BeginTransactionAsync();
        var search = new GraphShortestPathSearch(a.Id, b.Id)
        { Limits = work ? new() { WorkUnits = limit } : new() { MemoryBytes = limit } };

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await database.FindShortestPathAsync(session, search));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        transaction.State.ShouldBe(TransactionState.Faulted);
        if (session.LastStatementMetrics is { } metrics)
        { metrics.Memory.RetainedBytes.ShouldBe(0); metrics.Memory.ActiveLeases.ShouldBe(0); }
        await transaction.RollbackAsync();
        await CheckDistanceAsync(database, session, new(a.Id, b.Id), 1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest path: guards and pre-cancellation leave a transaction active (#1536)")]
    public async Task FindShortestPathAsync_InvalidArguments_ShouldRefuseBeforeAdmission()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("guards", new());
        var database = await engine.CreateDatabaseAsync("graph");
        var other = await engine.CreateDatabaseAsync("other");
        await using var session = await database.CreateSessionAsync();
        await using var foreign = await other.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await database.FindShortestPathAsync(null!, default));
        var binding = await Should.ThrowAsync<DatabaseException>(async () => await database.FindShortestDistanceAsync(foreign, default));
        binding.Message.ShouldContain("CG07005", Case.Sensitive); // Existing typed-session guard is message-only.
        var invalid = await Should.ThrowAsync<DatabaseException>(async () => await database.FindShortestPathAsync(session, new(default, default, (GraphDirection)255)));
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        var cancellation = await Should.ThrowAsync<OperationCanceledException>(async () => await database.FindShortestDistanceAsync(session, default, canceled.Token));
        cancellation.CancellationToken.ShouldBe(canceled.Token);
        transaction.State.ShouldBe(TransactionState.Active);
        await transaction.RollbackAsync();
        await foreign.DisposeAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await other.FindShortestDistanceAsync(foreign, default));
        await database.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await database.FindShortestPathAsync(session, default));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Shortest path: cancellation interrupts candidate work, frontier dispatch and reconstruction (#1536)")]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SearchAsync_RecordedCancellation_ShouldStopAtCanceledWork(int cancelAt)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("cancellation", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        await EdgeAsync(database, session, a, b);
        using var cancellation = new CancellationTokenSource();
        var budget = new GraphPlanExecutor.ExpansionBudget
        {
            OnConsume = count => { if (count == cancelAt) { cancellation.Cancel(); } },
            OnFrontier = _ => { if (cancelAt == 0) { cancellation.Cancel(); } },
        };

        // Act
        await database.RunAsync(session, async operation =>
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
                await GraphShortestPathFinder.SearchAsync(database, operation, new(a.Id, b.Id), budget, true, cancellation.Token));
            error.CancellationToken.ShouldBe(cancellation.Token);
            return true;
        }, CancellationToken.None);

        // Assert
        cancellation.IsCancellationRequested.ShouldBeTrue();
        budget.Physical.ShouldBe(cancelAt == 0 ? 2 : cancelAt);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest path: cancellation after public statement admission aborts the transaction (#1536)")]
    public async Task FindShortestPathAsync_CanceledDuringTypeResolution_ShouldAbortAndReleaseMemory()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("public-cancel", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        await EdgeAsync(database, session, a, b);
        await using var transaction = await session.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        database.Catalog.BeforeRecordRead = () => { reads++; cancellation.Cancel(); };

        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await database.FindShortestPathAsync(session, new(a.Id, b.Id, RelationshipType: "KNOWS"), cancellation.Token));
        database.Catalog.BeforeRecordRead = null;

        // Assert
        reads.ShouldBeGreaterThan(0);
        error.CancellationToken.ShouldBe(cancellation.Token);
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        await transaction.RollbackAsync();
        await CheckDistanceAsync(database, session, new(a.Id, b.Id), 1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest distance: engine ceilings and selective ranges bound physical work (#1536)")]
    public async Task FindShortestDistanceAsync_LowEngineWorkCeiling_ShouldSkipUnselectedTypes()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("engine-budget", new() { QueryWorkBudget = 3 });
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var a = await NodeAsync(database, session, "a");
        var b = await NodeAsync(database, session, "b");
        await EdgeAsync(database, session, a, b);
        for (int i = 0; i < 20; i++) { await EdgeAsync(database, session, a, b, "OTHER"); }
        var search = new GraphShortestPathSearch(a.Id, b.Id, RelationshipType: "KNOWS");
        long decoded = database.Store.IncidentRecordsDecoded;

        // Act / Assert
        (await database.FindShortestDistanceAsync(session, search)).ShouldBe(1);
        (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(1);
        session.LastStatementMetrics!.Physical.ShouldBe(3);
        var pathBudget = await Should.ThrowAsync<DatabaseException>(async () => await database.FindShortestPathAsync(session, search));
        pathBudget.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        var wildcardBudget = await Should.ThrowAsync<DatabaseException>(async () => await database.FindShortestDistanceAsync(session, search with { RelationshipType = null }));
        wildcardBudget.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest distance: retained state omits explored entity payloads (#1536)")]
    public async Task FindShortestDistanceAsync_WideReachableNodes_ShouldRetainOnlyVertexState()
    {
        // Arrange: same exhaustive reachable set for the existing traversal and new distance API.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("payload-memory", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var root = await database.CreateNodeAsync(session, ["Hub"], cancellationToken: timeout.Token);
        var isolated = await NodeAsync(database, session, "isolated");
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 128)) +
            "] INSERT (:Leaf {payload:$payload})", new Dictionary<string, object?> { ["payload"] = new string('w', 3000) }, timeout.Token);
        await session.ExecuteAsync("MATCH (a:Hub),(b:Leaf) INSERT (a)-[:KNOWS]->(b)", cancellationToken: timeout.Token);
        long before = long.MaxValue, after = long.MaxValue;

        // Act
        for (int round = 0; round < 3; round++)
        {
            int count = 0;
            await foreach (var _ in database.TraverseAsync(session, new(root.Id, RelationshipType: "KNOWS", MaxDepth: int.MaxValue), timeout.Token)) { count++; }
            count.ShouldBe(128);
            before = Math.Min(before, session.LastStatementMetrics!.Memory.PeakRetainedBytes);
            (await database.FindShortestDistanceAsync(session, new(root.Id, isolated.Id, RelationshipType: "KNOWS"), timeout.Token)).ShouldBeNull();
            after = Math.Min(after, session.LastStatementMetrics!.Memory.PeakRetainedBytes);
            session.LastStatementMetrics.Physical.ShouldBe(130);
            session.LastStatementMetrics.Viable.ShouldBe(128);
            session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        }

        // Assert: the distance search has no copy of the 128 wide explored payloads.
        after.ShouldBeLessThan(before / 4);
        after.ShouldBeLessThan(40_000);
        _output.WriteLine($"Minimum of 3 accounted peaks: traversal={before}, shortest distance={after} bytes; physical=130, viable=128.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Shortest distance: parallel relationship types do not enlarge retained search state (#1536)")]
    public async Task FindShortestDistanceAsync_ParallelTypes_ShouldKeepMemoryIndependentOfTypeCount()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("parallel-types", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var small = await NodeAsync(database, session, "small");
        var large = await NodeAsync(database, session, "large");
        var leaf = await NodeAsync(database, session, "leaf");
        var isolated = await NodeAsync(database, session, "isolated");
        await EdgeAsync(database, session, small, leaf, "T000");
        for (int i = 0; i < 128; i++) { await EdgeAsync(database, session, large, leaf, $"T{i:000}"); }
        long smallPeak = long.MaxValue, largePeak = long.MaxValue;

        // Act
        for (int round = 0; round < 3; round++)
        {
            (await database.FindShortestDistanceAsync(session, new(small.Id, isolated.Id), timeout.Token)).ShouldBeNull();
            smallPeak = Math.Min(smallPeak, session.LastStatementMetrics!.Memory.PeakRetainedBytes);
            session.LastStatementMetrics.Physical.ShouldBe(3);
            (await database.FindShortestDistanceAsync(session, new(large.Id, isolated.Id), timeout.Token)).ShouldBeNull();
            largePeak = Math.Min(largePeak, session.LastStatementMetrics!.Memory.PeakRetainedBytes);
            session.LastStatementMetrics.Physical.ShouldBe(130);
            session.LastStatementMetrics.Viable.ShouldBe(1);
        }

        // Assert
        largePeak.ShouldBe(smallPeak);
        _output.WriteLine($"Minimum of 3 accounted peaks: 1 type={smallPeak}, 128 types={largePeak} bytes; physical=3/130, viable=1/1.");
    }

    private static ValueTask<GraphNode> NodeAsync(GraphDatabase database, GraphDatabaseSession session, string name)
        => database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = name });

    private static ValueTask<GraphRelationship> EdgeAsync(GraphDatabase database, GraphDatabaseSession session, GraphNode from, GraphNode to, string type = "KNOWS")
        => database.CreateRelationshipAsync(session, from.Id, to.Id, type);

    private static async Task CheckDistanceAsync(GraphDatabase database, GraphDatabaseSession session, GraphShortestPathSearch search, int? expected)
    {
        (await database.FindShortestDistanceAsync(session, search)).ShouldBe(expected);
        var result = await database.FindShortestPathAsync(session, search);
        (result?.Distance).ShouldBe(expected);
        if (expected is { } distance)
        {
            var path = result!.Path;
            path.Nodes.Count.ShouldBe(distance + 1);
            path.Relationships.Count.ShouldBe(distance);
            path.Nodes[0].Id.ShouldBe(search.Start);
            path.Nodes[^1].Id.ShouldBe(search.Target);
        }
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }
}
