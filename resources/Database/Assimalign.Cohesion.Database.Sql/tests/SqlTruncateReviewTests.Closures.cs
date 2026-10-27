using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTruncateReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - TRUNCATE: a missing cascaded target requires retry while a missing named target retains its error (#1716)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_DependentDisappearsDuringLockWait_ShouldClassifyByWrittenTargets(bool named, bool rename)
    {
        // Arrange: the competing DDL queues on c before TRUNCATE closes over p and c.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-missing-child", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await using var ddl = await database.CreateSessionAsync(timeout.Token);
        await using var truncator = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(holder, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(holder, "CREATE TABLE c (id INT, pid INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(holder, "INSERT INTO p VALUES (1)", timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await ExecAsync(holder, "INSERT INTO c VALUES (2,1)", timeout.Token);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = written =>
        {
            if (written.EventName != "LockWaitStart") { return; }
            if (Interlocked.Increment(ref waits) == 1) { first.TrySetResult(); }
            else { second.TrySetResult(); }
        };
        Task? changing = null;
        Task? truncating = null;
        try
        {
            changing = ExecAsync(ddl, rename ? "ALTER TABLE c RENAME TO renamed" : "DROP TABLE c", timeout.Token);
            await first.Task.WaitAsync(timeout.Token);
            truncating = ExecAsync(truncator, named ? "TRUNCATE p, c CASCADE" : "TRUNCATE p CASCADE", timeout.Token);
            await second.Task.WaitAsync(timeout.Token);
            Volatile.Read(ref waits).ShouldBe(2);

            // Act
            await transaction.CommitAsync(timeout.Token);
            await changing;
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await truncating);

            // Assert
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(named ? "CS01051" : "CS04064");
            diagnostic.Message.ShouldBe(named ? string.Format(ErrorMessages.TableMissing, "dbo", "c") : ErrorMessages.TruncateClosureChanged);
            (await RowsAsync(holder, "SELECT id FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            await ExecAsync(truncator, "TRUNCATE p CASCADE", timeout.Token);
            (await RowsAsync(holder, "SELECT id FROM p", timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (transaction.State == TransactionState.Active) { await transaction.RollbackAsync(CancellationToken.None); }
            if (changing is not null) { try { await changing; } catch (Exception) { /* Preserve the primary failure. */ } }
            if (truncating is not null) { try { await truncating; } catch (Exception) { /* Observe the expected conflict. */ } }
        }
    }
}
