using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeReviewRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE overlay anchor probes see committed creators and queued updates (#1735)")]
    [InlineData(IsolationLevel.Snapshot, 0)]
    [InlineData(IsolationLevel.ReadCommitted, 0)]
    [InlineData(IsolationLevel.Snapshot, 1)]
    [InlineData(IsolationLevel.ReadCommitted, 1)]
    [InlineData(IsolationLevel.Snapshot, 2)]
    [InlineData(IsolationLevel.ReadCommitted, 2)]
    public async Task MatchAsync_OverlayAnchorRace_ShouldConflictOrRematch(IsolationLevel isolation, int change)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        if (change != 0) { await database.CreateNodeAsync(writer, ["Person"], Properties(7), token); }
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(isolation, token);
        if (change == 0) { await database.CreateNodeAsync(writer, ["Person"], Properties(7), token); }
        await using var updating = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        if (change != 0)
        {
            await writer.ExecuteAsync(change == 1 ? "MATCH (n:Person {id: 7}) SET n.id = 8" : "MATCH (n:Person {id: 7}) REMOVE n:Person", cancellationToken: token);
        }

        // Act: update cases hold the writer so the matcher must queue.
        var pending = database.RunAsync(reader, operation => ReadAsync(database, operation, Identity(7), token), token).AsTask();
        if (change != 0) { pending.IsCompleted.ShouldBeFalse(); }
        await updating.CommitAsync(token);

        // Assert
        if (isolation == IsolationLevel.Snapshot)
        {
            (await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending)).Diagnostics.Single().Code.ShouldBe("CG04024");
            await transaction.RollbackAsync(token);
        }
        else
        {
            (await pending).Matches.Count.ShouldBe(change == 0 ? 1 : 0);
            await transaction.CommitAsync(token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE refreshes ReadCommitted after a logical pattern wait (#1735)")]
    public async Task MatchAsync_PatternWait_ShouldRefreshAfterGrant()
    {
        // Arrange: a raw pattern holder isolates this wait from the database writer wait.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        var identity = Identity(7);
        var blocker = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        await database.Coordinator.LockManager.AcquireAsync(blocker.Sequence, identity.Resource, LockMode.Exclusive, token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        try
        {
            // Act
            var pending = database.RunAsync(session, operation => GraphMergeConcurrency.MatchAsync(database, operation, identity,
                (view, _) => ValueTask.FromResult(new GraphMergeRead<bool>([view.IsVisible(blocker.Sequence)], [])), token), token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            await database.Coordinator.CommitAsync(blocker, token);

            // Assert: the probe includes the holder that committed during the wait.
            (await pending).Matches.ShouldBe([true]);
        }
        finally
        {
            if (blocker.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(blocker, token); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE cross-check catches disappearing matches with an incomplete entity set (#1735)")]
    public async Task MatchAsync_UnderreportedEntities_ShouldStillCheckMatchCount()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        await database.CreateNodeAsync(writer, ["Person"], Properties(7), token);
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await writer.ExecuteAsync("MATCH (n:Person) SET n.id = 8", cancellationToken: token);

        // Act: deliberately under-report entities while returning both complete match sets.
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(database.RunAsync(reader,
            operation => GraphMergeConcurrency.MatchAsync(database, operation, Identity(7), (view, _) =>
            {
                var matches = database.Store.GetNodes("Person", view).Where(n => GraphValueComparer.Instance.Equals(n.Properties["id"], 7)).Select(n => n.Id).ToArray();
                return ValueTask.FromResult(new GraphMergeRead<ulong>(matches, []));
            }, token), token).AsTask());

        // Assert
        error.Diagnostics.Single().Code.ShouldBe("CG04024");
        await transaction.RollbackAsync(token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE pattern entries consume the request or engine memory budget (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_DistinctPatternBudget_ShouldRefuseAndRelease(bool engineLimit)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new() { QueryMemoryBudget = engineLimit ? 1024 : 256L * 1024 * 1024 });
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        var budget = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 1024);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(database.RunAsync(session, async operation =>
        {
            if (!engineLimit) { operation.QueryBudget = budget; }
            try
            {
                for (int i = 0; i < 1000; i++)
                { await GraphMergeConcurrency.MatchAsync(database, operation, Identity(i), Empty, token); }
            }
            finally { budget = operation.QueryBudget!; }
            return true;
        }, token).AsTask());

        // Assert
        error.Diagnostics.Single().Code.ShouldBe("CG05020");
        budget.Memory.PeakRetainedBytes.ShouldBeGreaterThan(0);
        budget.Memory.RetainedBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE escalates bounded pattern state and still serializes phantoms (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task MatchAsync_ManyPatterns_ShouldEscalateAndProtectPhantoms(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var first = await database.CreateSessionAsync(cancellationToken: token);
        await using var second = await database.CreateSessionAsync(cancellationToken: token);
        await using var owner = await first.BeginTransactionAsync(isolation, token);
        await using var waiter = await second.BeginTransactionAsync(isolation, token);
        var budget = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 90_000);
        await database.RunAsync(first, async operation =>
        {
            operation.QueryBudget = budget;
            for (int i = 0; i < 1000; i++)
            { await GraphMergeConcurrency.MatchAsync(database, operation, Identity(i), Empty, token); }
            return true;
        }, token);
        bool coarseHeld = !database.Coordinator.LockManager.TryAcquire(waiter.Context.Sequence, LockResource.Object(ulong.MaxValue - 3), LockMode.IntentExclusive);

        // Act: escalation persists across statements of this transaction.
        await database.CreateNodeAsync(first, ["Person"], Properties(7), token);
        var pending = database.RunAsync(second, operation => ReadAsync(database, operation, Identity(7), token), token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await owner.CommitAsync(token);

        // Assert
        if (isolation == IsolationLevel.Snapshot)
        {
            (await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending)).Diagnostics.Single().Code.ShouldBe("CG04024");
            await waiter.RollbackAsync(token);
        }
        else
        {
            (await pending).Matches.Count.ShouldBe(1);
            await waiter.CommitAsync(token);
        }
        coarseHeld.ShouldBeTrue();
        budget.Memory.PeakRetainedBytes.ShouldBeInRange(640, 90_000);
        budget.Memory.RetainedBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE carries retained lock charges without pinning completed query workspaces (#1735)")]
    public async Task MatchAsync_RetainedPatternsAcrossStatements_ShouldChargeWithoutPinningWorkspaces()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var first = new GraphPlanExecutor.ExpansionBudget();
        var next = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 1024);
        var identity = Identity(7);

        // Act: force a value-accounting workspace in the first query, then retain its pattern.
        await database.RunAsync(session, async operation =>
        {
            operation.QueryBudget = first;
            using var values = new GraphMemoryOwner(first.Memory);
            values.RetainValue(new Dictionary<string, object?> { ["value"] = new string('x', 100_000) }, token);
            await GraphMergeConcurrency.MatchAsync(database, operation, identity, Empty, token);
            return true;
        }, token);

        // Assert: locks remain transaction-owned, but their old query workspace does not.
        transaction.State.ShouldBe(TransactionState.Active);
        first.Memory.ActiveLeases.ShouldBe(0);
        first.Memory.RetainedBytes.ShouldBe(0);
        first.Memory.RetainedBufferCapacity.ShouldBe(0);
        var error = await Should.ThrowAsync<DatabaseException>(database.RunAsync(session, operation =>
        {
            operation.QueryBudget = next;
            return GraphMergeConcurrency.MatchAsync(database, operation, identity, Empty, token);
        }, token).AsTask());
        error.Diagnostics.Single().Code.ShouldBe("CG05020");
        next.Memory.ActiveLeases.ShouldBe(0);
        next.Memory.RetainedBytes.ShouldBe(0);
        await transaction.RollbackAsync(token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE reuses statement views while retaining the writer (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task MatchAsync_RepeatedRows_ShouldReuseSnapshots(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(isolation, token);
        var views = new List<TransactionSnapshot>();
        var identity = Identity(7);

        // Act
        await database.RunAsync(session, async operation =>
        {
            for (int i = 0; i < 3; i++)
            {
                await GraphMergeConcurrency.MatchAsync(database, operation, identity, (view, _) =>
                { views.Add(view); return Empty(view, token); }, token);
                await operation.Updates.CreateNodeAsync(["Pending"], Properties(i), token);
            }
            return true;
        }, token);

        // Assert: one decision view and (only for Snapshot) one latest view per statement.
        views.Distinct(ReferenceEqualityComparer.Instance).Count().ShouldBe(isolation == IsolationLevel.Snapshot ? 2 : 1);
        await transaction.RollbackAsync(token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE caches only the latest evaluated identity and forgets it at statement end (#1735)")]
    public async Task MatchAsync_RepeatedEvaluatedPattern_ShouldComputeIdentityOnce()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        var key = new object();
        int computations = 0;

        // Act
        for (int statement = 0; statement < 2; statement++)
        {
            await database.RunAsync(session, async operation =>
            {
                for (int row = 0; row < 3; row++)
                {
                    await GraphMergeConcurrency.MatchAsync(database, operation, key, Create, Empty, token);
                }
                computations.ShouldBe(statement * 2 + 1);
                await GraphMergeConcurrency.MatchAsync(database, operation, new object(), () => Identity(8), Empty, token);
                await GraphMergeConcurrency.MatchAsync(database, operation, key, Create, Empty, token);
                return true;
            }, token);
        }

        // Assert: one initial computation and one after changing keys, in each statement.
        computations.ShouldBe(4);
        GraphMergePatternIdentity Create() { computations++; return Identity(7); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE restores overlay read views after latest-probe success or failure (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_LatestProbeExit_ShouldRestoreReadView(bool fail)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        await using var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        var committed = await database.CreateNodeAsync(writer, ["Other"], Properties(9), token);

        // Act
        await database.RunAsync(reader, async operation =>
        {
            var overlay = await operation.Updates.CreateNodeAsync(["Pending"], Properties(8), token);
            int probes = 0;
            var pending = GraphMergeConcurrency.MatchAsync(database, operation, Identity(7), (_, _) =>
            {
                probes++;
                operation.Updates.FindNode(overlay.Id.Value).ShouldNotBeNull();
                (operation.Updates.FindNode(committed.Id.Value) is not null).ShouldBe(probes == 2);
                if (fail && probes == 2) { throw new ArithmeticException("latest probe"); }
                return ValueTask.FromResult(new GraphMergeRead<ulong>([], []));
            }, token).AsTask();
            if (fail) { await Should.ThrowAsync<ArithmeticException>(pending); }
            else { await pending; }

            // Assert: both exits restore the fixed begin view and preserve overlays.
            operation.Updates.FindNode(committed.Id.Value).ShouldBeNull();
            operation.Updates.FindNode(overlay.Id.Value).ShouldNotBeNull();
            probes.ShouldBe(2);
            return true;
        }, token);
        await transaction.RollbackAsync(token);
    }

    private static Dictionary<string, object?> Properties(int id) => new() { ["id"] = id };
    private static GraphMergePatternIdentity Identity(int id) => new([new(null, ["Person"], Properties(id))], []);
    private static ValueTask<GraphMergeRead<ulong>> Empty(TransactionSnapshot view, CancellationToken token)
        => ValueTask.FromResult(new GraphMergeRead<ulong>([], []));
    private static ValueTask<GraphMergeRead<ulong>> ReadAsync(GraphDatabase database, GraphOperation operation, GraphMergePatternIdentity identity, CancellationToken token)
        => GraphMergeConcurrency.MatchAsync(database, operation, identity, async (_, ct) =>
        {
            var budget = operation.QueryBudget ??= new();
            var ids = new List<ulong>();
            await foreach (var node in operation.Updates.StreamAnchorAsync(new(0, "Person", null, null), budget, ct))
            {
                if (node.Properties.TryGetValue("id", out var value) && GraphValueComparer.Instance.Equals(value, 7)) { ids.Add(node.Id); }
            }
            return new GraphMergeRead<ulong>(ids, ids.Select(id => new GraphMergeEntity(true, id)).ToArray());
        }, token);
}
