using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeConcurrencyTests
{
    private static readonly IReadOnlyDictionary<string, object?> _empty = new Dictionary<string, object?>();

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE creators serialize empty patterns and reject stale Snapshot decisions (#1735)")]
    [InlineData(IsolationLevel.Snapshot, false)]
    [InlineData(IsolationLevel.ReadCommitted, false)]
    [InlineData(IsolationLevel.Snapshot, true)]
    [InlineData(IsolationLevel.ReadCommitted, true)]
    public async Task MatchAsync_ConcurrentCreators_ShouldCreateExactlyOnce(IsolationLevel isolation, bool overlap)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", timeout.Token);

        // Act / Assert
        await RaceCreatorsAsync(database, isolation, overlap, timeout.Token);
    }

    private static async Task RaceCreatorsAsync(GraphDatabase database, IsolationLevel isolation, bool overlap, CancellationToken token, bool threaded = false)
    {
        await using var first = await database.CreateSessionAsync(cancellationToken: token);
        await using var second = await database.CreateSessionAsync(cancellationToken: token);
        await using var winner = await first.BeginTransactionAsync(isolation, token);
        await using var loser = await second.BeginTransactionAsync(isolation, token);
        var identity = Identity(overlap ? ["Person", "Named"] : ["Person"]);
        var competing = Identity(["Person"]);
        int creates = 0, probes = 0;
        await MergeAsync(first, identity, overlap ? "Named" : "Person");
        database.Coordinator.LockManager.TryAcquire(loser.Context.Sequence, identity.Resource, LockMode.Exclusive).ShouldBeFalse();

        // Calling this async method runs to its queued writer wait before returning. Both
        // transactions already have their snapshots, and the first still owns both locks.
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = threaded ? Task.Run(async () =>
        {
            var task = MergeAsync(second, competing, "Person").AsTask();
            ReportQueued(task, queued);
            return await task;
        }, token) : MergeAsync(second, competing, "Person").AsTask();
        if (threaded) { await queued.Task.WaitAsync(token); }
        pending.IsCompleted.ShouldBeFalse();
        probes.ShouldBe(2 * (isolation == IsolationLevel.Snapshot ? 1 : 0) + (isolation == IsolationLevel.ReadCommitted ? 1 : 0));
        await winner.CommitAsync(token);
        if (isolation == IsolationLevel.Snapshot)
        {
            var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending);
            error.Diagnostics.Single().Code.ShouldBe("CG04024");
            error.Message.ShouldNotContain("CG04024", Case.Sensitive);
            loser.State.ShouldBe(TransactionState.Faulted);
            await AssertReleasedAsync(database, [identity, competing], token);
            await loser.RollbackAsync(token);
        }
        else
        {
            (await pending).ShouldBeFalse();
            await loser.CommitAsync(token);
        }
        creates.ShouldBe(1);
        database.Store.GetNodes("Person", database.LatestSnapshot(winner.Context)).Count.ShouldBe(1);
        await AssertReleasedAsync(database, [identity, competing], token);

        async ValueTask<bool> MergeAsync(GraphDatabaseSession session, GraphMergePatternIdentity key, string label)
            => await database.RunAsync(session, async operation =>
            {
                var read = await GraphMergeConcurrency.MatchAsync(database, operation, key,
                    (view, ct) => { Interlocked.Increment(ref probes); return Probe(database, label, view, ct); }, token);
                if (read.Matches.Count != 0) { return false; }
                Interlocked.Increment(ref creates);
                await operation.Updates.CreateNodeAsync(overlap ? ["Person", "Named"] : ["Person"], new Dictionary<string, object?> { ["id"] = 7 }, token);
                await operation.Updates.PublishAsync(token);
                return true;
            }, token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE matcher races ordinary delete and refreshes only ReadCommitted (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task MatchAsync_ConcurrentDeleter_ShouldConflictOrRematch(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        var node = await database.CreateNodeAsync(writer, ["Person"], new Dictionary<string, object?> { ["id"] = 7 }, token);
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var snapshot = await reader.BeginTransactionAsync(isolation, token);
        await using var deleting = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await database.DeleteNodeAsync(writer, node.Id, token);
        var identity = Identity(["Person"]);

        // Act
        var pending = ReadAsync(database, reader, identity, token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await deleting.CommitAsync(token);

        // Assert
        if (isolation == IsolationLevel.Snapshot)
        {
            (await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending)).Diagnostics.Single().Code.ShouldBe("CG04024");
            await AssertReleasedAsync(database, [identity], token);
            await snapshot.RollbackAsync(token);
        }
        else
        {
            (await pending).Matches.ShouldBeEmpty();
            await snapshot.CommitAsync(token);
        }
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE bound endpoints serialize directed and undirected relationship phantoms (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task MatchAsync_OverlappingRelationshipCreators_ShouldConflictOrMatch(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var first = await database.CreateSessionAsync(cancellationToken: token);
        var from = await database.CreateNodeAsync(first, ["Person"], cancellationToken: token);
        var to = await database.CreateNodeAsync(first, ["Person"], cancellationToken: token);
        await using var second = await database.CreateSessionAsync(cancellationToken: token);
        await using var winner = await first.BeginTransactionAsync(isolation, token);
        await using var loser = await second.BeginTransactionAsync(isolation, token);
        GraphMergeNodeIdentity[] nodes = [new(from.Id.Value, ["Person"], _empty), new(to.Id.Value, ["Person"], _empty)];
        var directed = new GraphMergePatternIdentity(nodes, [new(0, 1, "KNOWS", false, null, _empty)]);
        var undirected = new GraphMergePatternIdentity(nodes, [new(1, 0, "KNOWS", true, null, _empty)]);
        await database.RunAsync(first, async operation =>
        {
            (await GraphMergeConcurrency.MatchAsync(database, operation, directed, ProbeEdge, token)).Matches.ShouldBeEmpty();
            await operation.Updates.CreateRelationshipAsync(from.Id, to.Id, "KNOWS", _empty, token);
            await operation.Updates.PublishAsync(token);
            return true;
        }, token);

        // Act
        var pending = database.RunAsync(second,
            operation => GraphMergeConcurrency.MatchAsync(database, operation, undirected, ProbeEdge, token), token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(token);

        // Assert
        if (isolation == IsolationLevel.Snapshot)
        {
            (await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending)).Diagnostics.Single().Code.ShouldBe("CG04024");
            await AssertReleasedAsync(database, [directed, undirected], token);
            await loser.RollbackAsync(token);
        }
        else
        {
            (await pending).Matches.Count.ShouldBe(1);
            await loser.CommitAsync(token);
        }
        (await database.Store.GetIncidentAsync(from.Id.Value, database.LatestSnapshot(winner.Context), token)).Count.ShouldBe(1);
        await AssertReleasedAsync(database, [directed, undirected], token);

        async ValueTask<GraphMergeRead<ulong>> ProbeEdge(TransactionSnapshot view, CancellationToken ct)
        {
            var edges = (await database.Store.GetIncidentAsync(from.Id.Value, view, ct))
                .Where(edge => edge.Type == "KNOWS" && edge.SourceId == from.Id.Value && edge.TargetId == to.Id.Value).ToArray();
            return new(edges.Select(edge => edge.Id).ToArray(), edges.SelectMany(edge => new GraphMergeEntity[]
                { new(true, edge.SourceId), new(true, edge.TargetId), new(false, edge.Id) }).ToArray());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE freshness includes every matched and bound node or relationship revision (#1735)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MatchAsync_ReplacedEntityWithRestoredValues_ShouldConflict(bool relationship, bool noMatch)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Person {id: 7})-[:KNOWS {id: 7}]->(:Person {id: 8})", cancellationToken: token);
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var entity = new GraphMergeEntity(!relationship, relationship
            ? (await database.Store.GetIncidentAsync(database.Store.GetNodes("Person", transaction.Context.Snapshot)[0].Id, transaction.Context.Snapshot, token))[0].Id
            : database.Store.GetNodes("Person", transaction.Context.Snapshot)[0].Id);
        var identity = relationship
            ? new GraphMergePatternIdentity([new(null, ["Person"], _empty), new(null, ["Person"], _empty)], [new(0, 1, "KNOWS", false, noMatch ? entity.Id : null, _empty)])
            : new GraphMergePatternIdentity([new(noMatch ? entity.Id : null, ["Person"], _empty)], []);
        // Two replacements restore the original property value. Value equality alone misses it.
        await writer.ExecuteAsync(relationship ? "MATCH ()-[r:KNOWS]->() SET r.id = 9" : "MATCH (n:Person {id: 7}) SET n.id = 9", cancellationToken: token);
        await writer.ExecuteAsync(relationship ? "MATCH ()-[r:KNOWS]->() SET r.id = 7" : "MATCH (n:Person {id: 9}) SET n.id = 7", cancellationToken: token);

        // Act
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(database.RunAsync(reader,
            operation => GraphMergeConcurrency.MatchAsync(database, operation, identity,
                (_, _) => ValueTask.FromResult(new GraphMergeRead<ulong>(noMatch ? [] : [entity.Id], noMatch ? [] : [entity])), token), token).AsTask());

        // Assert: bound endpoints are protected even with no whole-pattern match.
        error.Diagnostics.Single().Code.ShouldBe("CG04024");
        await AssertReleasedAsync(database, [identity], token);
        await transaction.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE freshness checks all matches and permits unrelated commits (#1735)")]
    public async Task MatchAsync_MultipleMatchesAndUnrelatedCommit_ShouldCheckEveryEntity()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await database.CreateSessionAsync(cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:Person {id: 7}), (:Person {id: 7})", cancellationToken: token);
        await using var reader = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        await writer.ExecuteAsync("INSERT (:Other {id: 7})", cancellationToken: token);

        // Act / Assert
        (await ReadAsync(database, reader, identity, token)).Matches.Count.ShouldBe(2);
        await transaction.CommitAsync(token);
        await using var stale = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        // Change the second match, not just the first candidate.
        var second = database.Store.GetNodes("Person", stale.Context.Snapshot)[1];
        await database.DeleteNodeAsync(writer, new GraphNodeId(second.Id), token);
        (await Should.ThrowAsync<DatabaseTransactionAbortedException>(ReadAsync(database, reader, identity, token).AsTask()))
            .Diagnostics.Single().Code.ShouldBe("CG04024");
        await AssertReleasedAsync(database, [identity], token);
        await stale.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE sees earlier creations in its transaction and statement overlay (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task MatchAsync_RepeatedRowsAndStatements_ShouldObserveOwnCreations(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(isolation, token);
        var identity = Identity(["Person"]);

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            async ValueTask<GraphMergeRead<ulong>> Read(TransactionSnapshot view, CancellationToken ct)
            {
                var stored = await Probe(database, "Person", view, ct);
                var pending = operation.Updates.GetNodes("Person", ct).Where(node => operation.Updates.IsCreated(true, node.Id)).Select(node => node.Id);
                return new(stored.Matches.Concat(pending).ToArray(), stored.Entities);
            }
            (await GraphMergeConcurrency.MatchAsync(database, operation, identity, Read, token)).Matches.ShouldBeEmpty();
            var node = await operation.Updates.CreateNodeAsync(["Person"], new Dictionary<string, object?> { ["id"] = 7 }, token);
            (await GraphMergeConcurrency.MatchAsync(database, operation, identity, Read, token)).Matches.ShouldBe([node.Id.Value]);
            await operation.Updates.PublishAsync(token);
            return true;
        }, token);
        (await ReadAsync(database, session, identity, token)).Matches.Count.ShouldBe(1);
        await transaction.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE exceptions and cancellation abort and release both locks (#1735)")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task MatchAsync_ProbeFailureOrCancellation_ShouldReleaseLocks(int failAt, bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        int probes = 0;
        if (failAt == 0) { cancellation.Cancel(); }

        // Act
        var pending = database.RunAsync(session, operation => GraphMergeConcurrency.MatchAsync(database, operation, identity,
            (_, _) =>
            {
                if (++probes == failAt)
                {
                    if (cancel) { cancellation.Cancel(); }
                    else { throw new ArithmeticException("probe failure"); }
                }
                return ValueTask.FromResult(new GraphMergeRead<ulong>([], []));
            }, cancellation.Token), token).AsTask();

        // Assert
        if (cancel || failAt == 0) { await Should.ThrowAsync<OperationCanceledException>(pending); }
        else { await Should.ThrowAsync<ArithmeticException>(pending); }
        transaction.State.ShouldBe(TransactionState.Faulted);
        await AssertReleasedAsync(database, [identity], token);
        await transaction.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - queued MERGE cancellation and session disposal release wait ownership (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_EndedWhileQueued_ShouldReleaseWaitOwnership(bool dispose)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var first = await database.CreateSessionAsync(cancellationToken: token);
        await using var owner = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        await ReadAsync(database, first, identity, token);
        await using var second = await database.CreateSessionAsync(cancellationToken: token);
        await using var waiting = await second.BeginTransactionAsync(IsolationLevel.Snapshot, token);

        // Act
        var pending = ReadAsync(database, second, identity, cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        if (dispose) { await second.DisposeAsync(); }
        else { cancellation.Cancel(); }
        await owner.RollbackAsync(token);

        // Assert
        await Should.ThrowAsync<Exception>(pending);
        await AssertReleasedAsync(database, [identity], token);
        await waiting.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE session disposal during either probe releases serialization (#1735)")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MatchAsync_DisposedDuringProbe_ShouldReleaseLocks(int pauseAt)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        var pending = database.RunAsync(session, operation => GraphMergeConcurrency.MatchAsync(database, operation, identity,
            async (_, ct) =>
            {
                if (++probes == pauseAt)
                {
                    entered.SetResult();
                    await resume.Task.WaitAsync(ct);
                }
                return new GraphMergeRead<ulong>([], []);
            }, token), token).AsTask();
        await entered.Task.WaitAsync(token);

        // Act
        try { await session.DisposeAsync(); }
        finally { resume.TrySetResult(); }

        // Assert
        await Should.ThrowAsync<DatabaseException>(pending);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE cancellation and disposal while waiting on the logical pattern release the writer (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_EndedWhilePatternQueued_ShouldReleaseBothResources(bool dispose)
    {
        // Arrange: a test-only raw owner isolates the logical-pattern wait from the writer wait.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        var identity = Identity(["Person"]);
        var blocker = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        await database.Coordinator.LockManager.AcquireAsync(blocker.Sequence, identity.Resource, LockMode.Exclusive, token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        try
        {
            var pending = ReadAsync(database, session, identity, cancellation.Token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            // Prove the MERGE already owns the writer; the pending wait is the pattern lock.
            database.Coordinator.LockManager.TryAcquire(blocker.Sequence, LockResource.Database(), LockMode.Exclusive).ShouldBeFalse();

            // Act
            if (dispose) { await session.DisposeAsync(); }
            else { cancellation.Cancel(); }

            // Assert
            await Should.ThrowAsync<Exception>(pending);
        }
        finally { await database.Coordinator.RollbackAsync(blocker, token); }
        await AssertReleasedAsync(database, [identity], token);
        await transaction.RollbackAsync(token);
        await AssertReleasedAsync(database, [identity], token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - MERGE end or cancellation between writer and pattern grants releases late ownership (#1735)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MatchAsync_EndedBetweenGrants_ShouldReleaseLatePatternGrant(bool dispose, bool autocommit)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var transaction = autocommit ? null : await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        int gates = 0, probes = 0;
        bool chargedBeforeEnd = false;
        var budget = new GraphPlanExecutor.ExpansionBudget();

        // Act
        var pending = database.RunAsync(session, operation =>
        {
            operation.QueryBudget = budget;
            return GraphMergeConcurrency.MatchAsync(database, operation, identity,
                (_, _) => { probes++; return ValueTask.FromResult(new GraphMergeRead<ulong>([], [])); }, cancellation.Token,
                async () =>
                {
                    gates++;
                    chargedBeforeEnd = budget.Memory.RetainedBytes > 0;
                    if (dispose) { await session.DisposeAsync(); }
                    else { cancellation.Cancel(); }
                });
        }, token).AsTask();

        // Assert: the uncontended pattern grant occurs after end/cancellation; no probe runs.
        await Should.ThrowAsync<Exception>(pending);
        gates.ShouldBe(1);
        probes.ShouldBe(0);
        chargedBeforeEnd.ShouldBeTrue();
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        await AssertReleasedAsync(database, [identity], token);
        if (transaction is not null) { await transaction.RollbackAsync(token); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE freshness charges physical work and aborts at the configured budget (#1735)")]
    public async Task MatchAsync_FreshnessWorkLimit_ShouldAbortAndReleaseLocks()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["id"] = 7 }, token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var identity = Identity(["Person"]);
        var budget = new GraphPlanExecutor.ExpansionBudget(workLimit: 0);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(database.RunAsync(session, operation =>
        {
            operation.QueryBudget = budget;
            return GraphMergeConcurrency.MatchAsync(database, operation, identity, (view, ct) => Probe(database, "Person", view, ct), token);
        }, token).AsTask());

        // Assert
        error.Diagnostics.Single().Code.ShouldBe("CG05004");
        budget.Consumed.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Faulted);
        await AssertReleasedAsync(database, [identity], token);
        await transaction.RollbackAsync(token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE autocommit empty and matched exits and transaction disposal release locks (#1735)")]
    public async Task MatchAsync_SuccessAndDisposal_ShouldReleaseLocks()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var database = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        var identity = Identity(["Person"]);

        // Act / Assert
        (await ReadAsync(database, session, identity, token)).Matches.ShouldBeEmpty();
        await AssertReleasedAsync(database, [identity], token);
        await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["id"] = 7 }, token);
        (await ReadAsync(database, session, identity, token)).Matches.Count.ShouldBe(1);
        await AssertReleasedAsync(database, [identity], token);
        await using (var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token))
        {
            (await ReadAsync(database, session, identity, token)).Matches.Count.ShouldBe(1);
            await using var other = await database.CreateSessionAsync(cancellationToken: token);
            var create = database.CreateNodeAsync(other, ["Other"], cancellationToken: token).AsTask();
            create.IsCompleted.ShouldBeFalse();
            await transaction.DisposeAsync();
            await create;
        }
        await AssertReleasedAsync(database, [identity], token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - MERGE threaded stress records queued creator conflicts and refreshes before stopping (#1735)")]
    public async Task MatchAsync_ThreadedCreatorStress_ShouldExerciseBothIsolationRaces()
    {
        // Arrange: a minimum interval followed by counter-driven completion, under a hang guard.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        int snapshotRaces = 0, committedRaces = 0;
        var clock = Stopwatch.StartNew();

        // Act: independent thread-pool actors drive each isolated database's winner and waiter.
        do
        {
            await using var database = await engine.CreateDatabaseAsync("race" + (snapshotRaces + committedRaces), token);
            var isolation = snapshotRaces <= committedRaces ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted;
            await RaceCreatorsAsync(database, isolation, overlap: true, token, threaded: true);
            if (isolation == IsolationLevel.Snapshot) { Interlocked.Increment(ref snapshotRaces); }
            else { Interlocked.Increment(ref committedRaces); }
        } while (clock.Elapsed < TimeSpan.FromMilliseconds(250) || snapshotRaces < 2 || committedRaces < 2);

        // Assert: each credited race asserted a queued writer, exactly one creation and release.
        snapshotRaces.ShouldBeGreaterThanOrEqualTo(2);
        committedRaces.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - queued progress may complete the actor before its assertion (#1735)")]
    public void ReportQueued_CompletionDuringPublication_ShouldAssertObservedState()
    {
        // Arrange: force completion synchronously inside publication, with no scheduler race.
        var pending = new TaskCompletionSource();
        var queued = new TaskCompletionSource();
        var completion = queued.Task.ContinueWith(_ => pending.SetResult(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        // Act
        ReportQueued(pending.Task, queued);

        // Assert
        pending.Task.IsCompletedSuccessfully.ShouldBeTrue();
        completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - queued assertion failure publishes progress before throwing (#1735)")]
    public void ReportQueued_AlreadyCompleted_ShouldPublishBeforeFailure()
    {
        // Arrange
        var queued = new TaskCompletionSource();

        // Act
        Should.Throw<ShouldAssertException>(() => ReportQueued(Task.CompletedTask, queued));

        // Assert
        queued.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private static void ReportQueued(Task task, TaskCompletionSource queued)
    {
        bool completed = task.IsCompleted;
        queued.SetResult();
        completed.ShouldBeFalse();
    }

    private static GraphMergePatternIdentity Identity(string[] labels)
        => new([new(null, labels, new Dictionary<string, object?> { ["id"] = 7 })], []);

    private static ValueTask<GraphMergeRead<ulong>> ReadAsync(GraphDatabase database, GraphDatabaseSession session,
        GraphMergePatternIdentity identity, CancellationToken token)
        => database.RunAsync(session, operation => GraphMergeConcurrency.MatchAsync(database, operation, identity,
            (view, ct) => Probe(database, "Person", view, ct), token), token);

    private static ValueTask<GraphMergeRead<ulong>> Probe(GraphDatabase database, string label, TransactionSnapshot view, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ids = database.Store.GetNodes(label, view).Where(node => node.Properties.TryGetValue("id", out var value)
            && GraphValueComparer.Instance.Equals(value, 7)).Select(node => node.Id).ToArray();
        return ValueTask.FromResult(new GraphMergeRead<ulong>(ids, ids.Select(id => new GraphMergeEntity(true, id)).ToArray()));
    }

    private static async Task AssertReleasedAsync(GraphDatabase database, GraphMergePatternIdentity[] identities, CancellationToken token)
    {
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        try
        {
            database.Coordinator.LockManager.TryAcquire(context.Sequence, LockResource.Database(), LockMode.Exclusive).ShouldBeTrue();
            foreach (var identity in identities)
            { database.Coordinator.LockManager.TryAcquire(context.Sequence, identity.Resource, LockMode.Exclusive).ShouldBeTrue(); }
        }
        finally { await database.Coordinator.RollbackAsync(context, token); }
    }
}
