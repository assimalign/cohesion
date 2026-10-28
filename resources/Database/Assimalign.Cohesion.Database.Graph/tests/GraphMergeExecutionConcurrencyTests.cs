using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeExecutionConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - executing MERGE creators conflict under Snapshot and rematch under ReadCommitted (#1735)")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task Execute_ConcurrentCreators_ShouldCreateExactlyOnce(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var first = await db.CreateSessionAsync(cancellationToken: token);
        await using var second = await db.CreateSessionAsync(cancellationToken: token);
        await using var winner = await first.BeginTransactionAsync(isolation, token);
        await using var loser = await second.BeginTransactionAsync(isolation, token);
        (await first.ExecuteAsync("MERGE (:P {id:1})", cancellationToken: token)).AffectedCount.ShouldBe(1);

        // Act
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pins = 0;
        var pending = db.RunAsync(second, async operation =>
        {
            operation.Context.Snapshot.IsVisible(winner.Context.Sequence).ShouldBeFalse();
            pins++;
            pinned.TrySetResult();
            await resume.Task.WaitAsync(token);
            return await GraphPlanExecutor.ExecuteAsync(db, operation,
                GraphQueryRequest.FromGql("MERGE (n:P {id:1}) ON MATCH SET n.hit=1 RETURN n.id").Statement, null, token);
        }, token).AsTask();
        await pinned.Task.WaitAsync(token);
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(token);
        resume.TrySetResult();

        // Assert
        pins.ShouldBe(1);
        if (isolation == IsolationLevel.Snapshot)
        {
            (await Should.ThrowAsync<DatabaseTransactionAbortedException>(pending)).Diagnostics[0].Code.ShouldBe("CG04024");
            await loser.RollbackAsync(token);
        }
        else
        {
            (await GraphMergeExecutionTests.Read(await pending)).ShouldHaveSingleItem()[0].ShouldBe(1L);
            await loser.CommitAsync(token);
        }
        (await GraphMergeExecutionTests.Rows(first, "MATCH (n:P) RETURN count(*)", token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE re-reads incoming entity values after a pattern wait (#1735)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_PatternWaitWithBoundValues_ShouldIncrementRefreshedVersion(bool relationship, bool create)
    {
        // Arrange: retain an incoming row and cached view from before the writer commits.
        // A raw pattern holder forces waited=true without weakening the production writer lock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(cancellationToken: token);
        await session.ExecuteAsync("CREATE (:P {c:0})-[:R {c:0}]->()", cancellationToken: token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var blocker = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startMerge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong id = 0;
        int waits = 0;
        long beforeProbe = 0;
        string target = relationship ? "r" : "a";
        var pending = db.RunAsync(session, async operation =>
        {
            var a = GraphDatabase.Materialize(operation.Updates.GetNodes("P", token).Single());
            var edge = (await db.Store.GetIncidentAsync(a.Id.Value, operation.Context.Snapshot, token)).Single();
            var r = GraphDatabase.Materialize(edge);
            id = relationship ? r.Id.Value : a.Id.Value;
            string merge = create ? "MERGE (:Fresh) ON CREATE" : "MERGE (a) ON MATCH";
            var stage = (GqlMergeStage)GraphQueryRequest.FromGql($"{merge} SET {target}.c={target}.c+1").Statement.GqlExpression.Stages.Single();
            var plan = new GraphPathPlan(stage.Path, new(0, null, null, null)) { Shape = GraphPathShape.Create(stage.Path) };
            var budget = operation.QueryBudget ??= new();
            await operation.Merge.RefreshAsync(false, token);
            var identity = new GraphMergePatternIdentity([new(create ? null : a.Id.Value,
                create ? ["Fresh"] : [], new Dictionary<string, object?>())], []);
            await db.Coordinator.LockManager.AcquireAsync(blocker.Sequence, identity.Resource, LockMode.Exclusive, token);
            captured.TrySetResult();
            await startMerge.Task.WaitAsync(token);
            await foreach (var row in GraphPlanExecutor.StreamMergeAsync(db, operation, stage, plan,
                [new Dictionary<string, object?> { ["a"] = a, ["r"] = r }], budget, null, new StrongBox<long>(), token,
                waited =>
                {
                    if (waited) { waits++; }
                    beforeProbe = budget.Consumed;
                    entered.TrySetResult();
                }))
            {
                (relationship ? ((Protocol.GraphRelationship)row[target]!).Properties : ((Protocol.GraphNode)row[target]!).Properties)["c"].ShouldBe(2L);
                (budget.Consumed - beforeProbe).ShouldBeGreaterThanOrEqualTo(2); // Both incoming entity reads consume work.
            }
            await operation.Updates.PublishAsync(token);
            return true;
        }, token).AsTask();
        await captured.Task.WaitAsync(token);
        pending.IsCompleted.ShouldBeFalse();

        // Act: publish c=1 while the merge's incoming row still contains c=0.
        var writer = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        try
        {
            var properties = new Dictionary<string, object?> { ["c"] = 1L };
            if (relationship) { await db.Store.UpdateRelationshipAsync(id, properties, writer, token); }
            else
            {
                var labels = new[] { "P" };
                var ids = labels.ToDictionary(label => label, label => db.Catalog.FindLabel(label, writer.Snapshot)!.Value.Id, StringComparer.Ordinal);
                await db.Store.UpdateNodeAsync(id, labels, properties, ids, writer, token);
            }
            await db.Coordinator.CommitAsync(writer, token);
        }
        finally
        {
            if (writer.State == TransactionState.Active) { await db.Coordinator.RollbackAsync(writer, token); }
        }
        startMerge.TrySetResult();
        await entered.Task.WaitAsync(token);
        pending.IsCompleted.ShouldBeFalse();
        waits.ShouldBe(1);
        // The MERGE owns the writer and is queued on the raw holder's pattern resource.
        db.Coordinator.LockManager.TryAcquire(blocker.Sequence, LockResource.Database(), LockMode.Exclusive).ShouldBeFalse();
        await db.Coordinator.CommitAsync(blocker, token);
        await pending;
        await transaction.CommitAsync(token);

        // Assert: both expression evaluation and replacement construction used the fresh value.
        waits.ShouldBe(1);
        var rows = await GraphMergeExecutionTests.Rows(session,
            relationship ? "MATCH ()-[n:R]->() RETURN n.c" : "MATCH (n:P) RETURN n.c", token);
        rows.Single()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - latest MERGE probes survive a dropped indexed anchor (#1735)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_DroppedIndex_ShouldScanLatestAndPreserveFreshness(bool phantom, bool rowEquality)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await db.CreateSessionAsync(cancellationToken: token);
        await writer.ExecuteAsync(phantom ? "INSERT (:P {id:1})" : "INSERT (:P {id:7})", cancellationToken: token);
        await GraphSchema.Open(db, writer).CreateIndexAsync("P", "by_id", "id", token);
        await using var reader = await db.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        string query = rowEquality ? "LET key=7 MERGE (n:P {id:key}) RETURN n.id" : "MERGE (n:P {id:7}) RETURN n.id";
        (await GraphMergeExecutionTests.Rows(reader, "EXPLAIN " + query, token)).Select(row => row[2]).ShouldContain("IndexSeek");
        await db.RunAsync(writer, async operation =>
        {
            await db.LockWriterAsync(operation, token);
            var label = db.Catalog.FindLabel("P", operation.Context.Snapshot)!.Value;
            await db.Store.DropIndexAsync("P", "id", operation.Context, token);
            await db.Catalog.DeleteIndexAsync(label.Id, "by_id", operation.Context, token);
            return true;
        }, token);
        if (phantom) { await writer.ExecuteAsync("INSERT (:P {id:7})", cancellationToken: token); }

        // Act / Assert
        if (phantom)
        {
            var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
                await reader.ExecuteAsync(query, cancellationToken: token));
            failure.Diagnostics[0].Code.ShouldBe("CG04024");
        }
        else
        { (await GraphMergeExecutionTests.Rows(reader, query, token)).ShouldHaveSingleItem()[0].ShouldBe(7L); }
        await transaction.RollbackAsync(token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE freshness includes anonymous matched nodes and relationships (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AnonymousEntityReplacedWithRestoredValues_ShouldConflict(bool relationship)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var writer = await db.CreateSessionAsync(cancellationToken: token);
        await writer.ExecuteAsync("INSERT (:P {id:1})-[:R {key:7}]->(:P {id:2})", cancellationToken: token);
        await using var reader = await db.CreateSessionAsync(cancellationToken: token);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        string match = relationship ? "MATCH ()-[e:R]->()" : "MATCH (e:P {id:2})";
        await writer.ExecuteAsync(match + " SET e.extra=8", cancellationToken: token);
        await writer.ExecuteAsync(match + " REMOVE e.extra", cancellationToken: token);

        // Act
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await reader.ExecuteAsync("MERGE (:P {id:1})-[:R {key:7}]->(:P {id:2})", cancellationToken: token));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CG04024");
        await transaction.RollbackAsync(token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - ReadCommitted MERGE refresh clears cached missing relationship types (#1735)")]
    public async Task MatchAsync_Refresh_ShouldDiscardTypeSelectionCache()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var reader = await db.CreateSessionAsync(cancellationToken: token);
        await using var writer = await db.CreateSessionAsync(cancellationToken: token);
        var pattern = new GqlRelationshipPattern("r", "Fresh", GqlPatternDirection.Outgoing, new Dictionary<string, GqlExpression>());
        var identity = new GraphMergePatternIdentity([new(null, [], new Dictionary<string, object?>())], []);

        // Act
        await db.RunAsync(reader, async operation =>
        {
            operation.Updates.SelectTypes(pattern).ShouldBeEmpty();
            await writer.ExecuteAsync("INSERT ()-[:Fresh]->()", cancellationToken: token);
            var read = await GraphMergeConcurrency.MatchAsync(db, operation, identity, (_, _) =>
                ValueTask.FromResult(new GraphMergeRead<int>([operation.Updates.SelectTypes(pattern)!.Length], [])), token);
            read.Matches.ShouldBe([1]);
            return true;
        }, token);

        // Assert
        (await GraphMergeExecutionTests.Rows(writer, "MATCH ()-[:Fresh]->() RETURN count(*)", token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE teardown races active memory accounting without releasing flow-owned leases (#1735)")]
    public async Task MatchAsync_DisposalStress_ShouldFenceLateAccessAndReleaseEveryLease()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("merge", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        int rounds = 0;
        long charges = 0, afterEnd = 0;
        var clock = Stopwatch.StartNew();

        // Act: minimum duration plus observed disposal/charge counters, with a hang ceiling.
        do
        {
            await using var session = await db.CreateSessionAsync(cancellationToken: token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int ended = 0;
            GraphOperation? running = null;
            var budget = new GraphPlanExecutor.ExpansionBudget();
            var pending = Task.Run(async () => await db.RunAsync(session, async operation =>
            {
                running = operation;
                operation.QueryBudget = budget;
                return await GraphMergeConcurrency.MatchAsync(db, operation,
                    new GraphMergePatternIdentity([new(null, [], new Dictionary<string, object?>())], []), (_, ct) =>
                    {
                        using var memory = new GraphMemoryOwner(budget.Memory);
                        entered.TrySetResult();
                        do
                        {
                            ct.ThrowIfCancellationRequested();
                            memory.Add(32); memory.Release(32);
                            Interlocked.Increment(ref charges);
                        } while (Volatile.Read(ref ended) == 0);
                        for (int i = 0; i < 100; i++)
                        { memory.Add(32); memory.Release(32); Interlocked.Increment(ref afterEnd); }
                        return ValueTask.FromResult(new GraphMergeRead<int>([], []));
                    }, token);
            }, token), token);
            await entered.Task.WaitAsync(token);
            try
            {
                await session.DisposeAsync();
                Should.Throw<DatabaseException>(() => { _ = running!.Merge; }).Diagnostics[0].Code.ShouldBe("CK04002");
            }
            finally { Volatile.Write(ref ended, 1); }
            await Should.ThrowAsync<DatabaseException>(pending);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            rounds++;
        } while (clock.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 2 || afterEnd < 200);

        // Assert
        rounds.ShouldBeGreaterThanOrEqualTo(2);
        charges.ShouldBeGreaterThan(0);
        afterEnd.ShouldBeGreaterThanOrEqualTo(200);
        await using var next = await db.CreateSessionAsync(cancellationToken: token);
        (await next.ExecuteAsync("MERGE (:After)", cancellationToken: token)).AffectedCount.ShouldBe(1);
    }
}
