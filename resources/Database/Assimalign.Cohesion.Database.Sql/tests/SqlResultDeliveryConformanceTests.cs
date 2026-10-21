using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlResultDeliveryConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: partial rows end with a coded Error and the same session is reused")]
    [InlineData("value")]
    [InlineData("unicode")]
    [InlineData("oversized")]
    [InlineData("enumeration")]
    [InlineData("uncoded")]
    [InlineData("dispose")]
    [InlineData("header")]
    public async Task ExecuteAsync_LateResultFailure_ShouldEndStatementAndReuseSession(string failure)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var result = new ResultDeliveryFailureSet(failure);
        serverSession.ExecuteResultOverride = () => result;

        // Act
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1").Encode());
        if (failure != "header")
        {
            await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader);
            await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow);
        }
        var error = ProtocolErrorMessage.Decode((await peer.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        serverSession.ExecuteResultOverride = null;
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 42").Encode());

        // Assert: no completion remained from the failed statement; this is the next header.
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(failure == "enumeration" ? "COHSQLE041:" : "COHSQLE045:", Case.Sensitive);
        result.Disposed.ShouldBeTrue();
        result.RowsVisited.ShouldBe(failure == "header" ? 0 : failure is "enumeration" or "uncoded" or "dispose" ? 1 : 2);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: the typed client discards partial rows and an explicit transaction is aborted")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_InjectedResultFailure_ShouldAbortExplicitTransactionAndReuseConnection(bool explicitTransaction)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = ServerTestHarness.DatabaseName, EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        if (explicitTransaction)
        {
            await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
            await connection.ExecuteAsync("INSERT INTO users VALUES (3, 'pending')", cancellationToken: TestTimeout.Token());
        }
        var result = new ResultDeliveryFailureSet("value");
        serverSession.ExecuteResultOverride = () => result;

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT id FROM users", cancellationToken: TestTimeout.Token()));
        serverSession.ExecuteResultOverride = null;

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("COHSQLE045:", Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE045");
        failure.ConnectionUsable.ShouldBeTrue();
        result.Disposed.ShouldBeTrue();
        if (explicitTransaction)
        {
            serverSession.DatabaseSession!.CurrentTransaction!.State.ShouldBe(TransactionState.Faulted);
            var refused = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync("SELECT id FROM users", cancellationToken: TestTimeout.Token()));
            refused.Message.ShouldStartWith("COHSQLT005:", Case.Sensitive);
            await connection.ExecuteAsync("ROLLBACK", cancellationToken: TestTimeout.Token());
        }
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: TestTimeout.Token())).Count.ShouldBe(2);
        await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("INSERT INTO users VALUES (4, 'later')", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("COMMIT", cancellationToken: TestTimeout.Token());
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: cancellation disposes the result and aborts the session transaction")]
    public async Task ExecuteAsync_CancelledEnumeration_ShouldDisposeResultAndEndTransaction()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var session = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await session.DatabaseSession!.BeginTransactionAsync(TestTimeout.Token());
        var result = new ResultDeliveryFailureSet("cancel");
        session.ExecuteResultOverride = () => result;

        // Act
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1").Encode());
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow);
        await session.DisposeAsync().AsTask().WaitAsync(TestTimeout.Token());

        // Assert
        result.Disposed.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        harness.Server.Sessions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: failed abort preserves the result error and releases operation admission")]
    public async Task AbortResultDeliveryAsync_FailedRollbackNotification_ShouldRetainCauseAndReleaseGate()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("result-abort-failure", new()
        {
            StorageStrategy = strategy, MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("app", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: TestTimeout.Token());
        await using var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t VALUES (2)", cancellationToken: TestTimeout.Token());
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var pageId = database.DataStorage.GetOwnerPages(table.ObjectId).ShouldHaveSingleItem();
        var storage = database.DataStorage;
        var other = Enumerable.Range(1, (int)storage.PageManager.PageCount - 1).Select(page => (PageId)(long)page)
            .First(page => page != pageId && storage.FreeSpaceMap.IsAllocated(page));
        int capacity = storage.BufferPoolCapacity;
        storage.BufferPoolCapacity = 1;
        using (storage.PageManager.GetPage(other)) { }
        storage.BufferPoolCapacity = capacity;
        var faults = strategy.Faults("app");
        faults.RottenPage = pageId;
        var cause = new DatabaseException("Injected result failure.");
        int aborts = 0;
        var original = database.Coordinator.OnUndoDeferred;
        database.Coordinator.OnUndoDeferred = () =>
        {
            aborts++;
            // The first abort holds operation admission; a reentrant abort must return without
            // taking the transaction end gate again or replacing the first failure.
            session.AbortResultDeliveryAsync(new DatabaseException("Reentrant abort.")).IsCompletedSuccessfully.ShouldBeTrue();
            throw new InvalidOperationException("Injected cleanup failure.");
        };
        try
        {
            // Act
            await session.AbortResultDeliveryAsync(cause);

            // Assert
            aborts.ShouldBe(1);
            transaction.CreateStatementRefusal().InnerException.ShouldBeSameAs(cause);
            transaction.State.ShouldBe(TransactionState.Faulted);
        }
        finally { database.Coordinator.OnUndoDeferred = original; faults.RottenPage = null; }
        await transaction.RollbackAsync(TestTimeout.Token());
        await using var next = await session.BeginTransactionAsync(TestTimeout.Token());
        await next.RollbackAsync(TestTimeout.Token());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: abort and disposal races release the session operation gate")]
    public async Task AbortResultDeliveryAsync_RacingDisposal_ShouldKeepFirstCauseAndReleaseResources()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("result-abort-race", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        var clock = Stopwatch.StartNew();
        int resultWon = 0, disposalWon = 0, iterations = 0;

        // Act: run for a minimum interval, then until both orders have actually won, with a ceiling.
        do
        {
            await using var session = await database.CreateSessionAsync(timeout.Token);
            var transaction = await session.BeginTransactionAsync(timeout.Token);
            var cause = new DatabaseException("Injected result failure.");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abort = Task.Run(async () => { await start.Task; if ((iterations & 1) == 0) { await Task.Yield(); } await session.AbortResultDeliveryAsync(cause); }, timeout.Token);
            var dispose = Task.Run(async () => { await start.Task; if ((iterations & 1) != 0) { await Task.Yield(); } await session.DisposeAsync(); }, timeout.Token);
            start.SetResult();
            await Task.WhenAll(abort, dispose).WaitAsync(timeout.Token);
            var refused = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(timeout.Token));
            if (ReferenceEquals(refused.InnerException, cause)) { resultWon++; } else { disposalWon++; }
            await session.AbortResultDeliveryAsync(cause); // closed-session early exit is idempotent
            iterations++;
        } while (clock.Elapsed < TimeSpan.FromMilliseconds(250) ||
            ((resultWon == 0 || disposalWon == 0) && clock.Elapsed < TimeSpan.FromSeconds(10)));

        // Assert
        resultWon.ShouldBeGreaterThan(0);
        disposalWon.ShouldBeGreaterThan(0);
        iterations.ShouldBe(resultWon + disposalWon);
        await using var reusable = await database.CreateSessionAsync(timeout.Token);
        await reusable.AbortResultDeliveryAsync(new DatabaseException("No transaction."));
        await using var fresh = await reusable.BeginTransactionAsync(timeout.Token);
        await fresh.RollbackAsync(timeout.Token);
    }
}
