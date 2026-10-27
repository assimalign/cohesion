using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTableRewriteTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: a lock waiter leaves the shared provisioning pump available for a peer migration")]
    public async Task RewriteTableAsync_IdleTableLockHolder_ShouldNotStallAnotherDatabaseMigration()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("rewrite-peer", new());
        var first = await engine.CreateDatabaseAsync("first", timeout.Token);
        var second = await engine.CreateDatabaseAsync("second", timeout.Token);
        await using var holder = await first.CreateSessionAsync(timeout.Token);
        await using var peer = await second.CreateSessionAsync(timeout.Token);
        await PrepareAsync(holder, timeout.Token);
        var column = new CompiledSchemaColumn("id", DatabaseType.Int32, false);
        var baseline = new CompiledSchemaTable("items", "Tests.Row", [column], null, [], []);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "second", false, [], [baseline], []);
        await second.ApplySchemaAsync(schema, timeout.Token);
        await ExecAsync(peer, "INSERT INTO items VALUES (1)", timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await ExecAsync(holder, "INSERT INTO t VALUES (2)", timeout.Token);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(first.Name);
        listener.Observed = written => { if (written.EventName == "LockWaitStart") { waited.TrySetResult(); } };
        var rewrite = first.RewriteTableAsync(Table(first), cancel.Token).AsTask();
        try
        {
            await waited.Task.WaitAsync(timeout.Token);
            var target = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "second", false, [],
                [new CompiledSchemaTable("items", "Tests.Row", [column], null,
                    [new CompiledSchemaIndex("ix_items_id", ["id"], false)], [])], []);

            // Act: prepare and run a real deferred migration while the first lock is held.
            await second.SchemaProvisioner.ApplyAsync(target, timeout.Token, deferDataScalingSteps: true);
            while (second.SchemaProvisioner.Deferred is not null) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }

            // Assert
            rewrite.IsCompleted.ShouldBeFalse();
            second.Catalog.PendingMigration.ShouldBeNull();
            second.OfflineError.ShouldBeNull();
            second.IndexManager.ExportRegistrations().ShouldHaveSingleItem().Definition.Name.ShouldBe("ix_items_id");
        }
        finally
        {
            cancel.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await rewrite);
        }
        await transaction.RollbackAsync(timeout.Token);
        await first.RewriteTableAsync(Table(first), timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: a caller holding the conflicting transaction receives the configured coded lock timeout")]
    public async Task RewriteTableAsync_CallerOwnsConflictingTransaction_ShouldFailCodedAndReleaseAdmission()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-lock-timeout", new() { LockTimeout = TimeSpan.FromMilliseconds(20) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (2)", timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(() => database.RewriteTableAsync(Table(database), timeout.Token).AsTask());

        // Assert: no elapsed-time assertion; the timeout's structured identity decides it.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04044");
        failure.InnerException.ShouldBeOfType<TransactionLockTimeoutException>();
        database.Coordinator.GetOpenContexts().ShouldHaveSingleItem();
        await transaction.RollbackAsync(timeout.Token);
        await ExecAsync(session, "ALTER TABLE t ADD n INT", timeout.Token);
        await database.RewriteTableAsync(Table(database), timeout.Token);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }
}
