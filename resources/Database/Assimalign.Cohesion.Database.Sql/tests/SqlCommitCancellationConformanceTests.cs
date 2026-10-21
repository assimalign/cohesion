using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlCommitCancellationConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Execute: cancellation at execution completion preserves affected count and durable success (#1246 proof)")]
    public async Task ExecuteAsync_CanceledAtExecutionCompletion_ShouldCommitAndPersist()
    {
        // Arrange: cancel inside the apply callback after its final effect, before the
        // session enters CommitAsync or its log. Restoring the old session token fails here.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = SqlDatabaseEngine.Create("execution-cancellation", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        int applies = 0;
        SqlPlanExecutor.AfterInsertApplied = () => { applies++; canceled.Cancel(); };

        // Act
        DatabaseResult result;
        try { result = await session.ExecuteAsync("INSERT INTO items VALUES (42)", cancellationToken: canceled.Token); }
        finally { SqlPlanExecutor.AfterInsertApplied = null; }

        // Assert
        applies.ShouldBe(1);
        canceled.IsCancellationRequested.ShouldBeTrue();
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        result.AffectedCount.ShouldBe(1);
        session.CurrentTransaction.ShouldBeNull();
        await VerifyAsync(database, timeout.Token);
        await session.DisposeAsync();
        await database.DisposeAsync();
        await VerifyAsync(await engine.OpenDatabaseAsync("test", timeout.Token), timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - CREATE TABLE: cancellation after catalog self-commit delivers success without a retry (#1246)")]
    public async Task ExecuteAsync_CanceledAfterDdlSelfCommit_ShouldDeliverSuccessAndOneTable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = SqlDatabaseEngine.Create("ddl-cancellation", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(cancellationToken: timeout.Token);
        int commits = 0;
        SqlPlanExecutor.AfterSelfCommit = () => { commits++; canceled.Cancel(); };

        // Act: the catalog committed durably; mandatory cleanup must still finish.
        DatabaseResult result;
        try { result = await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: canceled.Token); }
        finally { SqlPlanExecutor.AfterSelfCommit = null; }

        // Assert: use the table immediately, without retrying CREATE TABLE.
        commits.ShouldBe(1);
        canceled.IsCancellationRequested.ShouldBeTrue();
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.LastStatementMetrics!.SelfCommits.ShouldBe(1);
        database.Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("items");
        (await session.ExecuteAsync("INSERT INTO items VALUES (42)", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", timeout.Token);
        reopened.Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("items");
        await VerifyAsync(reopened, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Commit: pre-cancellation preserves the transaction; cancellation during explicit or automatic commit preserves durable success (#1246 regression guard)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommitAsync_CancellationAtEndBoundary_ShouldPreserveOutcome(bool explicitTransaction, bool sqlControl)
    {
        // Regression guard for late-flush cancellation and explicit ends; the execution-
        // completion case above proves the session fix. Only durable flushes survive reopen.
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = SqlDatabaseEngine.Create("commit-cancellation", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: token);
        using var canceled = new CancellationTokenSource();
        int flushes = 0;
        void CancelDuringCommit()
        {
            flushes++;
            canceled.Cancel();
        }
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(token) : null;
        if (transaction is not null)
        {
            using var early = new CancellationTokenSource();
            early.Cancel();
            if (sqlControl)
            {
                await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("COMMIT;", cancellationToken: early.Token));
            }
            else
            {
                await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(early.Token));
            }
            transaction.State.ShouldBe(TransactionState.Active);
            session.CurrentTransaction.ShouldBeSameAs(transaction);
        }

        // Act: the journal callback cancels after the commit record has been appended.
        try
        {
            if (!explicitTransaction) { FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = CancelDuringCommit; }
            token = canceled.Token;
            await session.ExecuteAsync("INSERT INTO items VALUES (42)", cancellationToken: token);
            if (transaction is not null)
            {
                FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = CancelDuringCommit;
                if (sqlControl) { await session.ExecuteAsync("COMMIT;", cancellationToken: token); }
                else { await transaction.CommitAsync(token); }
            }
        }
        finally { FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = null; }

        // Assert: both the outcome and the session remain usable, and the write survives reopen.
        canceled.IsCancellationRequested.ShouldBeTrue();
        flushes.ShouldBe(1);
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Committed); }
        session.CurrentTransaction.ShouldBeNull();
        token = timeout.Token;
        await VerifyAsync(database, token);
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", token);
        await VerifyAsync(reopened, token);
    }

    private static async Task VerifyAsync(SqlDatabase database, CancellationToken token)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken: token);
        await using var result = (await session.ExecuteAsync("SELECT id FROM items", cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(token)) { row.GetInt32(0).ShouldBe(42); rows++; }
        rows.ShouldBe(1);
    }
}
