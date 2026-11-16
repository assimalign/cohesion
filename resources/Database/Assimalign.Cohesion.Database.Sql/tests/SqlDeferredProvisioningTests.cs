using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed partial class SqlDeferredProvisioningTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Joint deferred: Build retains all old objects, reserves touched tables and serves other tables")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_PopulatedMigration_ShouldDeferAndAdmitOnlyOtherTables(bool fileBacked)
    {
        // Arrange: the target adds a nullable column, an index, a CHECK and NOT NULL.
        string path = Path.Combine(Path.GetTempPath(), "cohesion-49a", Guid.NewGuid().ToString("N"));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema desired = Desired();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kinds = new ConcurrentQueue<SqlSchemaMigrationOperationKind>();
        long counted = 0;
        try
        {
            storage = await SeedAsync(storage, fileBacked ? path : null, desired, timeout.Token);
            var builder = Builder(storage, fileBacked ? path : null);
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = database =>
                {
                    database.SchemaProvisioner.BeforeDeferredStep = (step, token) =>
                    {
                        kinds.Enqueue(step.Operation.Kind);
                        entered.TrySetResult();
                        release.Wait(token);
                    };
                    database.SchemaProvisioner.DeferredProgress = (rows, _) => Interlocked.Exchange(ref counted, Math.Max(counted, rows));
                };
                return new RecordingWorker(engine);
            });
            builder.AddDatabase(desired);

            // Act: Build returns with the worker paused and its lock already held.
            await using var engine = await builder.BuildAsync(timeout.Token);
            var database = await engine.OpenDatabaseAsync("pending", timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            database.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
            items.FindColumn("Note").ShouldBeNull();
            var pending = database.Catalog.PendingMigration.ShouldNotBeNull();
            pending.Steps.Select(step => step.Kind).ShouldBe(["Joint/AlterColumn", "Joint/AddColumn", "Joint/AddIndex", "Joint/AddConstraint", "Joint/RewriteOwner"]);
            pending.Steps.Select(step => step.Position).ShouldBe([1, 2, 3, 4, 5]);
            database.Provisioning.ShouldNotBeNull().IsRunning.ShouldBeTrue();
            await using var blocked = await database.CreateSessionAsync(timeout.Token);
            await using var other = await database.CreateSessionAsync(timeout.Token);
            await using var cteReader = await database.CreateSessionAsync(timeout.Token);
            Task<DatabaseResult> waiting = blocked.ExecuteAsync("INSERT INTO items (Id, Value) VALUES (1000, NULL)", cancellationToken: timeout.Token).AsTask();
            Task<DatabaseResult> waitingCte = cteReader.ExecuteAsync(
                "WITH c AS (SELECT Value FROM items) SELECT Value FROM c", cancellationToken: timeout.Token).AsTask();
            int writes = 0;
            var elapsed = Stopwatch.StartNew();
            try
            {
                do
                {
                    await other.ExecuteAsync("INSERT INTO other (Id) VALUES (1)", cancellationToken: timeout.Token);
                    writes++;
                    waiting.IsCompleted.ShouldBeFalse();
                    waitingCte.IsCompleted.ShouldBeFalse();
                    elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
                }
                while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || writes < 10);
            }
            finally
            {
                release.Set();
            }
            var rejection = await Should.ThrowAsync<DatabaseException>(async () => await waiting);
            await using var cteResult = (await waitingCte).ShouldBeAssignableTo<DatabaseResultSet>();
            cteResult.Columns[0].IsNullable.ShouldBeFalse();
            rejection.Message.ShouldContain("Value");
            rejection.Message.ShouldContain("null");
            await WaitDoneAsync(database, timeout.Token);

            // Assert: binding happened after all three expensive steps published.
            writes.ShouldBeGreaterThanOrEqualTo(10);
            kinds.ShouldHaveSingleItem().ShouldBe(SqlSchemaMigrationOperationKind.AlterColumn);
            database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
            Interlocked.Read(ref counted).ShouldBe(130);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Provisioning.ShouldBeNull();
            database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
            database.Catalog.TryGetTable("dbo", "items", out items).ShouldBeTrue();
            items.FindColumn("Value").ShouldNotBeNull().IsNullable.ShouldBeFalse();
            items.Constraints.ShouldContain(constraint => constraint.Name == "ck_value");
            database.Catalog.GetIndexes(items.ObjectId).Count.ShouldBe(2);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - 49a: existing empty tables complete indexes, constraints and tightening during Build")]
    public async Task BuildAsync_ExistingEmptyTable_ShouldCompleteEveryStepInline()
    {
        // Arrange: the table has an empty primary-key tree but no data pages.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token, populate: false);
        int applied = 0;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database =>
            {
                database.SchemaProvisioner.StepApplied = _ => Interlocked.Increment(ref applied);
                database.SchemaProvisioner.BeforeDeferredStep = (_, _) => throw new InvalidOperationException("An empty table was deferred.");
            };
            return new RecordingWorker(engine);
        });

        // Act
        await using var engine = await builder.AddDatabase(desired).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("pending", timeout.Token);

        // Assert
        applied.ShouldBe(4);
        database.Provisioning.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Joint deferred: a crash during preparation resumes only the matching target")]
    public async Task BuildAsync_CrashDuringPreparation_ShouldResumeMatchingTargetAndConverge()
    {
        // Arrange: capture durable images at a real batch boundary, without publishing its tree.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        var captured = new TaskCompletionSource<CrashCaptureSqlStorageStrategy>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.DeferredProgress = (rows, total) =>
            {
                if (database.Provisioning?.Operation == "Joint/AlterColumn" && rows == 64)
                {
                    captured.TrySetResult(storage.CaptureDurableImages());
                    release.Wait(timeout.Token);
                }
            };
            return new RecordingWorker(engine);
        });
        builder.AddDatabase(desired);
        CrashCaptureSqlStorageStrategy crash;
        await using (var engine = await builder.BuildAsync(timeout.Token))
        {
            try
            {
                crash = await captured.Task.WaitAsync(timeout.Token);
                var database = await engine.OpenDatabaseAsync("pending", timeout.Token);
                database.Provisioning.ShouldNotBeNull().RowsProcessed.ShouldBe(64);
                database.Provisioning.TotalRows.ShouldBeNull();
                await using var other = await database.CreateSessionAsync(timeout.Token);
                string values = string.Join(",", Enumerable.Range(1, 1000).Select(value => $"({value})"));
                await other.ExecuteAsync($"INSERT INTO other (Id) VALUES {values}", cancellationToken: timeout.Token);
                await other.ExecuteAsync("CREATE TABLE scratch (Id INT)", cancellationToken: timeout.Token);
                database.Coordinator.Checkpoint();
                crash = storage.CaptureDurableImages();
            }
            finally
            {
                release.Set();
            }
        }

        // Act: only the same target declaration may resume unpublished joint work.
        await using var restarted = await Builder(crash).AddDatabase(desired).BuildAsync(timeout.Token);
        var recovered = await restarted.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(recovered, timeout.Token);

        // Assert: no duplicate or partial tree and a complete trusted marker.
        recovered.Catalog.PendingMigration.ShouldBeNull();
        recovered.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
        await using var reader = await recovered.CreateSessionAsync(timeout.Token);
        await using var result = (DatabaseResultSet)await reader.ExecuteAsync("SELECT Id FROM items WHERE Value = 65", cancellationToken: timeout.Token);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(timeout.Token))
        {
            row.GetValue(0).ShouldBe(65);
            rows++;
        }
        rows.ShouldBe(1);
        reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
        await using var otherResult = (DatabaseResultSet)await reader.ExecuteAsync("SELECT Id FROM other WHERE Id = 999", cancellationToken: timeout.Token);
        int otherRows = 0;
        await foreach (var row in otherResult.GetRowsAsync(timeout.Token))
        {
            row.GetValue(0).ShouldBe(999);
            otherRows++;
        }
        otherRows.ShouldBe(1);
        reader.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Joint deferred: a failed CHECK retains the old schema online and a rolled-back Build opens")]
    public async Task BuildAsync_InvalidExistingRows_ShouldKeepOldSchemaOnlineWithCode()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlSchema desired = Desired(failing: true);
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        var builder = Builder(storage);

        // Act
        await using var engine = await builder.AddDatabase(desired).AddDatabase("healthy").BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(database, timeout.Token);

        // Assert: all old objects remain published, including their schema marker.
        database.IsOffline.ShouldBeFalse();
        database.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03402");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldNotBe(desired.Compile().Hash);
        database.Catalog.TryGetTable("dbo", "items", out var items).ShouldBeTrue();
        items.FindColumn("Note").ShouldBeNull();
        items.FindColumn("Value")!.IsNullable.ShouldBeTrue();
        items.Constraints.ShouldBeEmpty();
        database.Catalog.GetIndexes(items.ObjectId).ShouldHaveSingleItem().IsPrimaryKey.ShouldBeTrue();
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await reader.ExecuteAsync("INSERT INTO items(Id,Value) VALUES(999,NULL)", cancellationToken: timeout.Token);
        var healthy = await engine.OpenDatabaseAsync("healthy", timeout.Token);
        await using var session = await healthy.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE live (Id INT)", cancellationToken: timeout.Token);
        await using var retry = await Builder(storage.CaptureDurableImages()).AddDatabase(Source()).BuildAsync(timeout.Token);
        var reopened = await retry.OpenDatabaseAsync("pending", timeout.Token);
        reopened.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        reopened.Catalog.PendingMigration.ShouldBeNull();
        reopened.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - 49a: the inline option completes populated-table migration during Build")]
    public async Task BuildAsync_InlineOption_ShouldCompleteEveryStepBeforeReturning()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        var builder = Builder(storage);
        builder.AddDatabase("pending", database => { database.Schema(desired); database.RunMigrationsInline = true; });

        // Act
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("pending", timeout.Token);

        // Assert
        database.Provisioning.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
        engine.DeclaredDatabases.Single().Result.ShouldNotBeNull().OperationCount.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Joint deferred: canceling the durable handoff releases every reservation and retries on restart")]
    public async Task BuildAsync_CanceledAtDeferredHandoff_ShouldDiscardAndConverge()
    {
        // Arrange: cancellation interrupts the handoff before any schema object publishes.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        int applied = 0;
        SqlDatabase? interrupted = null;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = stage =>
            {
                if (stage != "Handoff") { return; }
                interrupted = database;
                Interlocked.Increment(ref applied);
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            };
            return new RecordingWorker(engine);
        });

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await builder.AddDatabase(desired).BuildAsync(cancellation.Token));
        interrupted.ShouldNotBeNull().Catalog.PendingMigration.ShouldBeNull();
        await using var retry = await Builder(storage.CaptureDurableImages()).AddDatabase(desired).BuildAsync(timeout.Token);
        var database = await retry.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        applied.ShouldBe(1);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Joint deferred: canceling Build after a pending reservation discards and retries on restart")]
    public async Task BuildAsync_CanceledAfterReservation_ShouldDiscardPendingAndConverge()
    {
        // Arrange: the second declaration cancels Build after the first reserved its table.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        int steps = 0;
        SqlDatabase? interrupted = null;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database =>
            {
                if (database.Name.ToString() == "pending") { interrupted = database; }
                database.SchemaProvisioner.BeforeDeferredStep = (_, _) => Interlocked.Increment(ref steps);
                if (database.Name.ToString() == "stop")
                {
                    cancellation.Cancel();
                }
            };
            return new RecordingWorker(engine);
        });

        // Act: the queue is canceled before the construction barrier releases it.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await builder.AddDatabase(desired).AddDatabase("stop").BuildAsync(cancellation.Token));
        steps.ShouldBe(0);
        interrupted.ShouldNotBeNull().Catalog.PendingMigration.ShouldBeNull();
        await using var retry = await Builder(storage.CaptureDurableImages()).AddDatabase(desired).BuildAsync(timeout.Token);
        var database = await retry.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - 49a: cancellation after a recovery scrub batch retains classification and the next Build converges")]
    public async Task BuildAsync_CanceledDuringRecovery_ShouldKeepJournalAndConverge()
    {
        // Arrange: durable uncommitted rows require more than one recovery scrub batch.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        SqlSchema desired = Desired();
        var storage = await SeedAsync(new CrashCaptureSqlStorageStrategy(), null, desired, timeout.Token);
        await using (var writer = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await writer.OpenDatabaseAsync("pending", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await using var transaction = await session.BeginTransactionAsync(timeout.Token);
            for (int row = 1000; row < 1200; row++)
            {
                await session.ExecuteAsync($"INSERT INTO items (Id, Value) VALUES ({row}, {row})", cancellationToken: timeout.Token);
            }
            storage = storage.CaptureDurableImages();
        }
        int durableChanges = 0;
        var interrupted = Builder(storage);
        interrupted.AddWorker(engine =>
        {
            engine.BeforeProvisioning = _ => storage.DurableChanged = () =>
            {
                Interlocked.Increment(ref durableChanges);
                cancellation.Cancel();
            };
            return new RecordingWorker(engine);
        });

        // Act: the callback is armed after physical recovery, at logical scrub admission.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await interrupted.AddDatabase("pending").BuildAsync(cancellation.Token));
        durableChanges.ShouldBeGreaterThan(0);
        storage = storage.CaptureDurableImages();
        await using var next = await Builder(storage).AddDatabase(desired).BuildAsync(timeout.Token);
        var recovered = await next.OpenDatabaseAsync("pending", timeout.Token);
        await WaitDoneAsync(recovered, timeout.Token);

        // Assert: no abandoned row was mistaken for committed after the interrupted scrub.
        await using var reader = await recovered.CreateSessionAsync(timeout.Token);
        await using var result = (DatabaseResultSet)await reader.ExecuteAsync("SELECT COUNT(*) FROM items", cancellationToken: timeout.Token);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(timeout.Token))
        {
            row.GetValue(0).ShouldBe(130L);
            rows++;
        }
        rows.ShouldBe(1);
    }

    private static SqlSchema Source() => SqlSchema.Create("pending", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<NullableItem>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); });
        schema.Table<Other>("other", table => { table.Column(row => row.Id); table.Index(row => row.Id); });
    });

    private sealed record NullableItem(int Id, int? Value);

    private static SqlSchema Desired(bool failing = false) => SqlSchema.Create("pending", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<Item>("items", table =>
        {
            table.Key(row => row.Id);
            table.Column(row => row.Value);
            table.Column(row => row.Note);
            table.Index(row => row.Value);
            table.Check("ck_value", failing ? "Value < 0" : "Value >= 0");
        });
        schema.Table<Other>("other", table => { table.Column(row => row.Id); table.Index(row => row.Id); });
    });

    private static async Task<CrashCaptureSqlStorageStrategy> SeedAsync(CrashCaptureSqlStorageStrategy storage,
        string? path, SqlSchema desired, CancellationToken cancellationToken, bool populate = true)
    {
        await using var engine = await Builder(storage, path).BuildAsync(cancellationToken);
        var database = await engine.CreateDatabaseAsync("pending", cancellationToken);
        SqlCompiledSchema target = desired.Compile();
        var tables = target.Tables.Select(table => table.Name != "items" ? table : new CompiledSchemaTable(table.Name, table.RowType,
            table.Columns.Where(column => column.Name != "Note").Select(column => column.Name == "Value" ? column with { IsNullable = true } : column).ToArray(),
            table.PrimaryKey, [], [])).ToArray();
        await database.ApplySchemaAsync(new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, target.Name, true, target.Types, tables, []), cancellationToken);
        await using var session = await database.CreateSessionAsync(cancellationToken);
        for (int row = 1; populate && row <= 130; row++)
        {
            await session.ExecuteAsync($"INSERT INTO items (Id, Value) VALUES ({row}, {row})", cancellationToken: cancellationToken);
        }
        return path is null ? storage.CaptureDurableImages() : storage;
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage, string? path = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("deferred");
        if (path is null)
        {
            builder.Options.StorageStrategy = storage;
        }
        else
        {
            builder.Options.RootPath = path;
        }
        return builder;
    }

    private static async Task WaitDoneAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        while (database.SchemaProvisioner.Deferred is not null)
        {
            await Task.Delay(1, cancellationToken);
        }
        database.OfflineError.ShouldBeNull();
    }

    private sealed record Item(int Id, int Value, string? Note);
    private sealed record Other(int Id);
}
