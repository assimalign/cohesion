using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        var pending = second.ExecuteAsync("MERGE (n:P {id:1}) ON MATCH SET n.hit=1 RETURN n.id", cancellationToken: token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(token);

        // Assert
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

    [Theory(DisplayName = "Cohesion Test [Graph] - latest MERGE probes survive a dropped indexed anchor (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DroppedIndex_ShouldScanLatestAndPreserveFreshness(bool phantom)
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
        (await GraphMergeExecutionTests.Rows(reader, "EXPLAIN MERGE (n:P {id:7}) RETURN n.id", token)).Select(row => row[2]).ShouldContain("IndexSeek");
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
                await reader.ExecuteAsync("MERGE (n:P {id:7}) RETURN n.id", cancellationToken: token));
            failure.Diagnostics[0].Code.ShouldBe("CG04024");
        }
        else
        { (await GraphMergeExecutionTests.Rows(reader, "MERGE (n:P {id:7}) RETURN n.id", token)).ShouldHaveSingleItem()[0].ShouldBe(7L); }
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
