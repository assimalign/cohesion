using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlProvisioningAdmissionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a prior writer's competing apply or verify fails before waiting for another apply")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_PriorWriterAwaitingCompetingApply_ShouldRefuseWithoutWaiting(bool verify)
    {
        // Arrange: T owns z_existing; A creates a_first and then queues for T's table.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var applyingToken = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var builder = SqlDatabaseEngine.CreateBuilder("admission");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("orders", timeout.Token);
        var existing = new CompiledSchemaTable("z_existing", "Existing", [new("id", DatabaseType.Int32, false)], null, [], []);
        await database.ApplySchemaAsync(Schema(existing), timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await holder.ExecuteAsync("INSERT INTO z_existing (id) VALUES (42)", cancellationToken: timeout.Token);
        var desired = Schema(
            new CompiledSchemaTable("a_first", "First", [new("id", DatabaseType.Int32, false)], null, [], []),
            new CompiledSchemaTable("z_existing", "Existing", [new("id", DatabaseType.Int32, false),
                new("note", DatabaseType.String, true)], null, [], []));
        int steps = 0;
        database.SchemaProvisioner.StepApplied = _ => Interlocked.Increment(ref steps);
        using var waits = new EventSourceRecorder(EventSource.GetSources().Single(source =>
            source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        Task<SqlSchemaMigrationResult> applying = database.ApplySchemaAsync(desired, applyingToken.Token).AsTask();
        int refusals = 0;
        try
        {
            await waits.WaitForAsync(written => written.EventName == "LockWaitStart" &&
                written.Payload is { Count: > 3 } payload && Equals(payload[0], "orders") && Equals(payload[3], "Exclusive"), timeout.Token);
            Volatile.Read(ref steps).ShouldBe(1);

            // Act: T's owner awaits B before committing. B must complete synchronously;
            // a semaphore wait would form A -> T -> B -> A outside the lock graph.
            refusals = await RefuseUntilExercisedAsync(database, desired, verify, timeout.Token);
            applying.IsCompleted.ShouldBeFalse();
            await transaction.CommitAsync(timeout.Token);
            await applying.WaitAsync(timeout.Token);
        }
        finally
        {
            applyingToken.Cancel();
            try
            {
                await applying;
            }
            catch (OperationCanceledException) when (applyingToken.IsCancellationRequested)
            {
            }
        }

        // Assert: both DDL steps and T's commit completed without cancellation.
        refusals.ShouldBeGreaterThanOrEqualTo(10);
        steps.ShouldBe(2);
        transaction.State.ShouldBe(TransactionState.Committed);
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Hash);
        await AssertRowAsync(holder, "z_existing", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a prior writer's competing apply or verify fails while the deferred worker owns the gate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_DeferredWorkerHoldingTable_ShouldRefuseWithoutWaiting(bool verify)
    {
        // Arrange: the worker reserves items while T writes an unrelated existing table.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = SqlDatabaseEngine.CreateBuilder("deferred-admission");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("orders", timeout.Token);
        var items = new CompiledSchemaTable("items", "Item", [new("id", DatabaseType.Int32, false)], null, [], []);
        var existing = new CompiledSchemaTable("z_existing", "Existing", [new("id", DatabaseType.Int32, false)], null, [], []);
        await database.ApplySchemaAsync(Schema(items, existing), timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await holder.ExecuteAsync("INSERT INTO items (id) VALUES (1)", cancellationToken: timeout.Token);
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await holder.ExecuteAsync("INSERT INTO z_existing (id) VALUES (42)", cancellationToken: timeout.Token);
        var desired = Schema(new CompiledSchemaTable(items.Name, items.RowType, items.Columns, null,
            [new CompiledSchemaIndex("ix_items", ["id"])], []), existing);
        int workerSteps = 0;
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
        {
            Interlocked.Increment(ref workerSteps);
            entered.TrySetResult();
            release.Wait(token);
        };
        int refusals = 0;
        try
        {
            // Act: use Build's deferred path on the real engine-owned worker.
            await database.SchemaProvisioner.ApplyAsync(desired, timeout.Token, deferDataScalingSteps: true);
            await entered.Task.WaitAsync(timeout.Token);
            var migration = database.SchemaProvisioner.Deferred.ShouldNotBeNull();
            database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            migration.Tables.ShouldContain(table.ObjectId);
            var context = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            try
            {
                database.Coordinator.LockManager.TryAcquire(context.Sequence, LockResource.Object(table.ObjectId), LockMode.IntentShared)
                    .ShouldBeFalse("the worker must hold the table before competing admission is tested");
            }
            finally
            {
                await database.Coordinator.RollbackAsync(context, CancellationToken.None);
            }
            refusals = await RefuseUntilExercisedAsync(database, desired, verify, timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        finally
        {
            release.Set();
            while (database.SchemaProvisioner.Deferred is not null)
            {
                await Task.Delay(1, timeout.Token);
            }
        }

        // Assert: T commits while the worker is paused, then the worker converges.
        refusals.ShouldBeGreaterThanOrEqualTo(10);
        workerSteps.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Committed);
        database.OfflineError.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Hash);
        await AssertRowAsync(holder, "z_existing", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - EXPLAIN: direct and derived deferred index admission waits and describes the published seek")]
    [InlineData("SELECT id FROM items WHERE id = 1;")]
    [InlineData("SELECT d.id FROM (SELECT id FROM items WHERE id = 1) d;")]
    public async Task Execute_ExplainDuringDeferredIndex_ShouldWaitAndDescribePublishedPlan(string query)
    {
        // Arrange: pause the existing worker hook after it reserves a populated table.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = SqlDatabaseEngine.CreateBuilder("explain-admission");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("orders", timeout.Token);
        var items = new CompiledSchemaTable("items", "Item", [new("id", DatabaseType.Int32, false)], null, [], []);
        await database.ApplySchemaAsync(Schema(items), timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await using var explainer = await database.CreateSessionAsync(timeout.Token);
        await reader.ExecuteAsync("INSERT INTO items (id) VALUES (1)", cancellationToken: timeout.Token);
        var desired = Schema(new CompiledSchemaTable(items.Name, items.RowType, items.Columns, null,
            [new CompiledSchemaIndex("ix_items", ["id"])], []));
        int workerSteps = 0;
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
        {
            Interlocked.Increment(ref workerSteps);
            entered.TrySetResult();
            release.Wait(token);
        };
        try
        {
            await database.SchemaProvisioner.ApplyAsync(desired, timeout.Token, deferDataScalingSteps: true);
            await entered.Task.WaitAsync(timeout.Token);
            using var waits = new EventSourceRecorder(EventSource.GetSources().Single(source =>
                source.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);

            // Act: both calls synchronously reach a real lock-manager wait before yielding.
            Task<DatabaseResult> reading = reader.ExecuteAsync(query, cancellationToken: timeout.Token).AsTask();
            Task<DatabaseResult> describing = explainer.ExecuteAsync("EXPLAIN " + query, cancellationToken: timeout.Token).AsTask();
            try
            {
                // Assert: neither can bind the pre-migration TableScan. No scheduling delay is used.
                waits.Events.Count(written => written.EventName == "LockWaitStart" &&
                    written.Payload is { Count: > 3 } payload && Equals(payload[0], "orders") &&
                    Equals(payload[3], "IntentShared")).ShouldBe(2);
                reading.IsCompleted.ShouldBeFalse();
                describing.IsCompleted.ShouldBeFalse();
                workerSteps.ShouldBe(1);
            }
            finally
            {
                release.Set();
            }

            await using var rows = (await reading.WaitAsync(timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
            int count = 0;
            await foreach (var row in rows.GetRowsAsync(timeout.Token))
            {
                row.GetValue(0).ShouldBe(1);
                count++;
            }
            count.ShouldBe(1);
            reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_items");
            await AssertIndexPlanAsync(await describing.WaitAsync(timeout.Token), timeout.Token);
            await AssertIndexPlanAsync(await explainer.ExecuteAsync("EXPLAIN " + query, cancellationToken: timeout.Token), timeout.Token);
            database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Hash);
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task AssertIndexPlanAsync(DatabaseResult result, CancellationToken cancellationToken)
    {
        await using var description = result.ShouldBeOfType<DatabasePlanDescription>();
        int seeks = 0;
        await foreach (var row in description.GetRowsAsync(cancellationToken))
        {
            row.GetString(2).ShouldNotBe("TableScan");
            if (row.GetString(2) == "IndexSeek")
            {
                row.GetString(3).ShouldBe("dbo.items");
                row.GetString(4).ShouldBe("ix_items");
                seeks++;
            }
        }
        seeks.ShouldBe(1);
    }

    private static SqlCompiledSchema Schema(params CompiledSchemaTable[] tables)
        => new(SqlCompiledSchema.CurrentFormat, "orders", false, [], tables, []);

    private static async Task<int> RefuseUntilExercisedAsync(SqlDatabase database, SqlCompiledSchema schema,
        bool verify, CancellationToken cancellationToken)
    {
        int refusals = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ValueTask<SqlSchemaMigrationResult> competing = verify
                ? database.VerifySchemaAsync(schema, attempt.Token)
                : database.ApplySchemaAsync(schema, attempt.Token);
            bool completed = competing.IsCompleted;
            if (!completed)
            {
                // Clean up the red regression without waiting for the test's deadline.
                attempt.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await competing);
            }
            completed.ShouldBeTrue("admission must refuse before any wait outside the lock manager");
            var refusal = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await competing);
            refusal.ObjectName.ShouldBe("orders");
            refusal.OwningSchema.ShouldBe("orders");
            refusal.Operation.ShouldBe(verify ? "VERIFY SCHEMA" : "APPLY SCHEMA");
            refusals++;
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        }
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || refusals < 10);
        return refusals;
    }

    private static async Task AssertRowAsync(SqlDatabaseSession session, string table, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT id FROM {table}", cancellationToken: cancellationToken);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            row.GetValue(0).ShouldBe(42);
            rows++;
        }
        rows.ShouldBe(1);
    }
}
