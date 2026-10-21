using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCommitCancellationConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Commit: pre-cancellation preserves the transaction; cancellation during explicit or automatic commit preserves durable success (#1246)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommitAsync_CancellationAtEndBoundary_ShouldPreserveOutcome(bool explicitTransaction, bool sqlControl)
    {
        // Arrange: only successful durable flushes survive the simulated reopen.
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
