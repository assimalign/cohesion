using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeThirdPassLifetimeTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - database shutdown during a MERGE probe preserves lifecycle diagnostics (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_DatabaseDisposedDuringProbe_ShouldReportLifecycle(bool disposedRead)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("third", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var identity = new GraphMergePatternIdentity([new(null, ["Person"], new Dictionary<string, object?>())], []);
        int probes = 0;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(db.RunAsync(session, operation =>
            GraphMergeConcurrency.MatchAsync<ulong>(db, operation, identity, async (_, _) =>
            {
                probes++;
                await engine.DisposeAsync();
                if (disposedRead) { throw new ObjectDisposedException("disposed probe"); }
                return new([], []);
            }, token), token).AsTask());

        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CG09019");
        probes.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MERGE parent queue session close reports ended and releases ownership (#1735)")]
    public async Task MatchAsync_SessionClosedInParentQueue_ShouldReportEnded()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("third", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        var identity = new GraphMergePatternIdentity([new(null, ["Person"], new Dictionary<string, object?>())], []);
        var parent = LockResource.Object(ulong.MaxValue - 3);
        var blocker = await db.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        await db.Coordinator.LockManager.AcquireAsync(blocker.Sequence, parent, LockMode.Exclusive, token);
        await using var session = await db.CreateSessionAsync(token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        int waits = 0, probes = 0;
        try
        {
            var pending = db.RunAsync(session, operation =>
            {
                operation.QueryBudget = budget;
                return GraphMergeConcurrency.MatchAsync<ulong>(db, operation, identity,
                    (_, _) => { probes++; return ValueTask.FromResult(new GraphMergeRead<ulong>([], [])); }, token,
                    onPatternWait: waited => { if (waited) { waits++; } });
            }, token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            db.Coordinator.LockManager.TryAcquire(blocker.Sequence, LockResource.Database(), LockMode.Exclusive).ShouldBeFalse();

            // Act
            await session.DisposeAsync();
            var failure = await Should.ThrowAsync<DatabaseException>(pending);

            // Assert
            failure.Diagnostics[0].Code.ShouldBe("CK04002");
            waits.ShouldBe(1);
            probes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            db.Coordinator.LockManager.TryAcquire(blocker.Sequence, LockResource.Database(), LockMode.Exclusive).ShouldBeTrue();
            db.Coordinator.LockManager.TryAcquire(blocker.Sequence, identity.Resource, LockMode.Exclusive).ShouldBeTrue();
        }
        finally { await db.Coordinator.RollbackAsync(blocker, token); }
    }


    [Theory(DisplayName = "Cohesion Test [Graph] - executing MERGE session close in the initial writer queue reports ended (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_SessionClosedInWriterQueue_ShouldReportEnded(bool explicitTransaction)
    {
        // Arrange: real execution takes the statement writer before entering its MERGE stage.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("third", new());
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var first = await db.CreateSessionAsync(token);
        await using var owner = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        await first.ExecuteAsync("CREATE (:Owner)", cancellationToken: token);
        await using var second = await db.CreateSessionAsync(token);
        await using var waiting = explicitTransaction ? await second.BeginTransactionAsync(IsolationLevel.Snapshot, token) : null;
        var pending = second.ExecuteAsync("UNWIND [1,2] AS i MERGE (:Person {id:i})", cancellationToken: token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await second.DisposeAsync();
        var failure = await Should.ThrowAsync<DatabaseException>(pending);

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == "CK04002");
        second.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        second.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        await owner.RollbackAsync(token);
        await using var checker = await db.CreateSessionAsync(token);
        (await checker.ExecuteAsync("MERGE (:Person {id:1})", cancellationToken: token)).AffectedCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Graph] - engine shutdown during UNWIND MERGE reports database disposal (#1735)")]
    public async Task Execute_EngineShutdownDuringMerge_ShouldReportLifecycle()
    {
        // Arrange: shut down on the second input after the first MERGE has acquired state.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        GraphDatabaseEngine? running = null;
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("third");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("shutdown", value =>
        {
            calls++;
            if (calls == 2) { running!.Dispose(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(token);
        running = engine;
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(session.ExecuteAsync(
            "UNWIND [1,2,3] AS i MERGE (:Person {id:shutdown(i)})", cancellationToken: token).AsTask());

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == "CG09019");
        calls.ShouldBe(2);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
    }

}
