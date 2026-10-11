using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlProvisioningIsolationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: canceling behind a prior writer compensates only its own work and releases admission")]
    public async Task ApplySchemaAsync_CanceledBehindPriorWriter_ShouldPreserveWriterAndAllowRetry()
    {
        // Arrange: an existing transaction owns the target before the migration starts.
        var builder = SqlDatabaseEngine.CreateBuilder("provisioning-cancellation");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("orders", CancellationToken.None);
        var existing = new CompiledSchemaTable("z_existing", "Existing", [new("id", DatabaseType.Int32, false)], null, [], []);
        var initial = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [], [existing], []);
        await database.ApplySchemaAsync(initial, TestTimeout.Token());
        await using var holder = await database.CreateSessionAsync(CancellationToken.None);
        await using var unrelated = await database.CreateSessionAsync(CancellationToken.None);
        await unrelated.ExecuteAsync("CREATE TABLE other (id INT)", cancellationToken: TestTimeout.Token());
        await using var transaction = await holder.BeginTransactionAsync(TestTimeout.Token());
        await holder.ExecuteAsync("INSERT INTO z_existing (id) VALUES (42)", cancellationToken: TestTimeout.Token());
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new CompiledSchemaTable("a_first", "First", [new("id", DatabaseType.Int32, false)], null, [], []),
                new CompiledSchemaTable("z_existing", "Existing", [new("id", DatabaseType.Int32, false),
                    new("note", DatabaseType.String, true)], null, [], [])], []);
        int steps = 0;
        database.SchemaProvisioner.StepApplied = step =>
        {
            step.Operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddTable);
            Interlocked.Increment(ref steps);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act: exercise unrelated writes while the apply waits for the existing writer.
        Task<SqlSchemaMigrationResult> applying = database.ApplySchemaAsync(desired, cancellation.Token).AsTask();
        var elapsed = Stopwatch.StartNew();
        int writes = 0;
        try
        {
            do
            {
                await unrelated.ExecuteAsync("INSERT INTO other (id) VALUES (1)", cancellationToken: TestTimeout.Token());
                writes++;
                applying.IsCompleted.ShouldBeFalse();
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            }
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || Volatile.Read(ref steps) == 0 || writes < 10);
        }
        finally
        {
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await applying);
        }
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert: only the migration's new table disappeared. Admission and locks are usable.
        steps.ShouldBe(1);
        writes.ShouldBeGreaterThanOrEqualTo(10);
        database.Catalog.TryGetTable("dbo", "a_first", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "z_existing", out var preserved).ShouldBeTrue();
        preserved.FindColumn("note").ShouldBeNull();
        await holder.ExecuteAsync("CREATE INDEX ix_preserved ON z_existing (id)", cancellationToken: TestTimeout.Token());
        database.SchemaProvisioner.StepApplied = null;
        await database.ApplySchemaAsync(desired, TestTimeout.Token());
        database.Catalog.TryGetIndex(preserved.ObjectId, "ix_preserved", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        await using var result = (Execution.QueryResultSet)await holder.ExecuteAsync("SELECT id FROM z_existing", cancellationToken: TestTimeout.Token());
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(TestTimeout.Token()))
        {
            row.GetValue(0).ShouldBe(42);
            rows++;
        }
        rows.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: table writers wait through completion or compensation while unrelated traffic progresses")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_ConcurrentWriters_ShouldLockOnlyTheAffectedTableUntilCompletion(bool failApply)
    {
        // Arrange: pause after publication, when the original statement has committed.
        var builder = SqlDatabaseEngine.CreateBuilder("provisioning-concurrency");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("orders", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var unrelated = await database.CreateSessionAsync(CancellationToken.None);
        await unrelated.ExecuteAsync("CREATE TABLE other (id INT)", cancellationToken: TestTimeout.Token());
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new CompiledSchemaTable("orders", "Order", [new("id", DatabaseType.Int32, false)],
                null, [new CompiledSchemaIndex("ix_orders_id", ["id"])], [])], []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int hooks = 0;
        database.SchemaProvisioner.StepApplied = step =>
        {
            if (step.Operation.Kind == SqlSchemaMigrationOperationKind.AddTable)
            {
                Interlocked.Increment(ref hooks);
                entered.SetResult();
                release.Wait(timeout.Token);
                if (failApply)
                {
                    throw new InvalidOperationException("Injected failure after table publication.");
                }
            }
        };
        Task<SqlSchemaMigrationResult> applying = Task.Run(async () => await database.ApplySchemaAsync(schema, timeout.Token));
        Task<Execution.QueryResult>? writing = null;
        int reads = 0;
        int writes = 0;

        // Act: run until both independent operations have actually executed repeatedly.
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            writing = writer.ExecuteAsync("INSERT INTO orders (id) VALUES (42)", cancellationToken: timeout.Token).AsTask();
            var elapsed = Stopwatch.StartNew();
            do
            {
                await unrelated.ExecuteAsync("INSERT INTO other (id) VALUES (1)", cancellationToken: timeout.Token);
                writes++;
                await using var result = (Execution.QueryResultSet)await unrelated.ExecuteAsync("SELECT id FROM other", cancellationToken: timeout.Token);
                await foreach (var row in result.GetRowsAsync(timeout.Token))
                {
                    row.GetValue(0).ShouldBe(1);
                    reads++;
                }
                writing.IsCompleted.ShouldBeFalse("the new table must remain protected between DDL steps");
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            }
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || writes < 10 || reads < 10);

            await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                await unrelated.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id)", cancellationToken: timeout.Token));
        }
        finally
        {
            release.Set();
            if (failApply)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await applying);
                failure.Message.ShouldContain("Every completed step was compensated.");
                if (writing is not null)
                {
                    await Should.ThrowAsync<DatabaseException>(async () => await writing);
                }
            }
            else
            {
                await applying;
                if (writing is not null)
                {
                    await writing;
                }
            }
        }

        // Assert: compensation released admission and table locks without admitting a write
        // to its temporary table. Subsequent ad-hoc commits survive a refused schema retry.
        hooks.ShouldBe(1);
        writes.ShouldBeGreaterThanOrEqualTo(10);
        reads.ShouldBeGreaterThanOrEqualTo(10);
        if (failApply)
        {
            database.Catalog.TryGetTable("dbo", "orders", out _).ShouldBeFalse();
            await writer.ExecuteAsync("CREATE TABLE orders (id INT)", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO orders (id) VALUES (42)", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id)", cancellationToken: timeout.Token);
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(schema, timeout.Token));
            database.Catalog.TryGetTable("dbo", "orders", out var preserved).ShouldBeTrue();
            database.Catalog.TryGetIndex(preserved.ObjectId, "ix_orders_id", out var index).ShouldBeTrue();
            index.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        }
        await using var rows = (Execution.QueryResultSet)await writer.ExecuteAsync("SELECT id FROM orders", cancellationToken: timeout.Token);
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(timeout.Token))
        {
            row.GetValue(0).ShouldBe(42);
            count++;
        }
        count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: compensation never deletes another session's committed row and index")]
    public async Task ApplySchemaAsync_WriterAndIndexBetweenSteps_ShouldPreventInterleavingWithoutDataLoss()
    {
        // Arrange: reproduce the reviewer's exact StepApplied interleaving. A protected
        // write times out; an unprotected one commits and must survive any compensation.
        var builder = SqlDatabaseEngine.CreateBuilder("provisioning-isolation");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("orders", CancellationToken.None);
        await using var adhoc = await database.CreateSessionAsync(CancellationToken.None);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new CompiledSchemaTable("orders", "Order", [new("id", DatabaseType.Int32, false)],
                null, [new CompiledSchemaIndex("ix_orders_id", ["id"])], [])], []);
        int hooks = 0;
        bool committed = false;
        bool indexCreated = false;
        database.SchemaProvisioner.StepApplied = step =>
        {
            if (step.Operation.Kind != SqlSchemaMigrationOperationKind.AddTable)
            {
                return;
            }
            hooks++;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try
            {
                adhoc.ExecuteAsync("INSERT INTO orders (id) VALUES (42)", cancellationToken: timeout.Token)
                    .AsTask().GetAwaiter().GetResult();
                committed = true;
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                adhoc.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id)", cancellationToken: TestTimeout.Token())
                    .AsTask().GetAwaiter().GetResult();
                indexCreated = true;
            }
            catch (DatabaseObjectLockedException)
            {
            }
        };

        // Act
        try
        {
            await database.ApplySchemaAsync(schema, TestTimeout.Token());
        }
        catch (SqlSchemaMigrationException)
        {
            // The old collision path compensates here, after both ad-hoc commits.
        }

        // Assert: a committed row/index must never have been compensated away.
        hooks.ShouldBe(1);
        database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        if (committed)
        {
            await using var rows = (Execution.QueryResultSet)await adhoc.ExecuteAsync("SELECT id FROM orders", cancellationToken: TestTimeout.Token());
            int count = 0;
            await foreach (var row in rows.GetRowsAsync(TestTimeout.Token()))
            {
                row.GetValue(0).ShouldBe(42);
                count++;
            }
            count.ShouldBe(1);
        }
        if (indexCreated)
        {
            database.Catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out var index).ShouldBeTrue();
            index.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        }
        committed.ShouldBeFalse("the apply must keep its newly published table locked through its last step");
        indexCreated.ShouldBeFalse("ordinary DDL must not interleave with an apply");
    }
}
