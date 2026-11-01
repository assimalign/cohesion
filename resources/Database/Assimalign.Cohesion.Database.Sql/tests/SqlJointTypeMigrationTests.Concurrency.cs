using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: disposal cancels prequeue reservations before closing storage")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WaitingForReservation_ShouldCancelJoinAndReopenOld(bool engineShutdown)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(timeout.Token);
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        await using var user = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await user.BeginTransactionAsync(timeout.Token);
        await user.ExecuteAsync("UPDATE items SET Value=5 WHERE Id=1", cancellationToken: timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        var applying = Task.Run(async () => await database.ApplySchemaAsync(Target().Compile(), timeout.Token), timeout.Token);
        await waiting.Task.WaitAsync(timeout.Token);

        // Act
        if (engineShutdown) { await engine.DisposeAsync().AsTask().WaitAsync(timeout.Token); }
        else { await database.DisposeAsync().AsTask().WaitAsync(timeout.Token); }
        await Should.ThrowAsync<OperationCanceledException>(async () => await applying.WaitAsync(timeout.Token));

        // Assert
        await using var reopened = await Builder(storage.CaptureDurableImages()).AddDatabase(Source()).BuildAsync(timeout.Token);
        var current = await reopened.OpenDatabaseAsync("typed", timeout.Token);
        current.Catalog.PendingMigration.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        await using var writer = await current.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        (await RowsAsync(writer, "SELECT Value FROM items WHERE Id=1", timeout.Token)).Single()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: a racing index cannot retain a dropped column and new-table FKs never lapse")]
    public async Task ApplySchemaAsync_RacingIndexAndNewTable_ShouldObserveOnlyPublishedConstraints()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("joint-index-race", new());
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        CompiledSchemaTable Orders(DatabaseType type) => new("orders", "orders",
            [new("note", type, true)], null, [], []);
        var users = new CompiledSchemaTable("users", "users",
            [new("id", DatabaseType.Int32, false), new("legacy", DatabaseType.Int32, true)], new("pk_users", ["id"]), [], []);
        await database.ApplySchemaAsync(Compiled(Orders(DatabaseType.Int32), users), timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO orders VALUES(7)", cancellationToken: timeout.Token);
        var target = Compiled(Orders(DatabaseType.Int64),
            new("users", "users", [users.Columns[0]], users.PrimaryKey, [], []),
            new("notes", "notes", [new("pid", DatabaseType.Int32, true)], null, [],
                [new("fk_notes", CompiledSchemaConstraintKind.Reference, ["pid"], "users", ["id"])]));
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(target, timeout.Token, deferDataScalingSteps: true);
        await entered.Task.WaitAsync(timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        try
        {
            // Act
            await Should.ThrowAsync<DatabaseException>(async () =>
                await writer.ExecuteAsync("INSERT INTO notes VALUES(999)", cancellationToken: timeout.Token));
            var index = writer.ExecuteAsync("CREATE INDEX ix ON users(legacy)", cancellationToken: timeout.Token).AsTask();
            await waiting.Task.WaitAsync(timeout.Token);
            index.IsCompleted.ShouldBeFalse();
            release.Set();
            await Should.ThrowAsync<DatabaseException>(async () => await index);
            await WaitDoneAsync(database, timeout.Token);

            // Assert
            await Should.ThrowAsync<DatabaseException>(async () =>
                await writer.ExecuteAsync("INSERT INTO notes VALUES(999)", cancellationToken: timeout.Token));
            database.Catalog.TryGetTable("dbo", "users", out var current).ShouldBeTrue();
            current.FindColumn("legacy").ShouldBeNull();
            database.Catalog.GetIndexes(current.ObjectId).ShouldNotContain(index => index.Name == "ix");
        }
        finally { release.Set(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: disposal and shutdown join every preparation phase")]
    [InlineData("OwnersDurable", false)]
    [InlineData("TreesDurable", false)]
    [InlineData("TablePrepared", false)]
    [InlineData("Validated", false)]
    [InlineData("Publishing", false)]
    [InlineData("OwnersDurable", true)]
    [InlineData("TreesDurable", true)]
    [InlineData("TablePrepared", true)]
    [InlineData("Validated", true)]
    [InlineData("Publishing", true)]
    public async Task DisposeAsync_DuringPreparation_ShouldReleaseAndReopenOld(string phase, bool engineShutdown)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = await SeedAsync(timeout.Token);
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database =>
            {
                CancellationToken execution = default;
                database.SchemaProvisioner.BeforeDeferredStep = (_, token) => execution = token;
                database.SchemaProvisioner.JointStage = stage =>
                {
                    if (stage != phase) { return; }
                    entered.TrySetResult();
                    execution.WaitHandle.WaitOne();
                    execution.ThrowIfCancellationRequested();
                };
            };
            return new RecordingWorker(owner);
        });
        await using var engine = await builder.AddDatabase(Target()).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);

        // Act
        if (engineShutdown) { await engine.DisposeAsync(); }
        else { await database.DisposeAsync(); }
        var image = storage.CaptureDurableImages();

        // Assert
        await using var reopened = await Builder(image).AddDatabase(Source()).BuildAsync(timeout.Token);
        var current = await reopened.OpenDatabaseAsync("typed", timeout.Token);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        current.Catalog.PendingMigration.ShouldBeNull();
        await using var session = await current.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1000,7)", cancellationToken: timeout.Token);
        await using var retry = await Builder(image.CaptureDurableImages()).AddDatabase(Target()).BuildAsync(timeout.Token);
        var migrated = await retry.OpenDatabaseAsync("typed", timeout.Token);
        await WaitDoneAsync(migrated, timeout.Token);
        migrated.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: a prequeue lock cycle is detected and a peer database progresses")]
    public async Task ApplySchemaAsync_UserLockCycle_ShouldDetectDeadlockWithoutStallingPeer()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        var peer = await engine.CreateDatabaseAsync("peer", timeout.Token);
        var source = Compiled(
            new("orders", "orders", [new("id", DatabaseType.Int32, false), new("note", DatabaseType.Int32, true)], new("pk_orders", ["id"]), [], []),
            new("users", "users", [new("id", DatabaseType.Int32, false), new("legacy", DatabaseType.Int32, true)], new("pk_users", ["id"]), [], []));
        var target = Compiled(
            new("orders", "orders", [new("id", DatabaseType.Int32, false), new("note", DatabaseType.Int64, true)], new("pk_orders", ["id"]), [], []),
            new("users", "users", [new("id", DatabaseType.Int32, false)], new("pk_users", ["id"]), [], []));
        SqlCompiledSchema Peer(SqlCompiledSchema schema) => new(schema.Format, "peer", true, schema.Types, schema.Tables, []);
        await database.ApplySchemaAsync(source, timeout.Token);
        await peer.ApplySchemaAsync(Peer(Source().Compile()), timeout.Token);
        await using var user = await database.CreateSessionAsync(timeout.Token);
        await user.ExecuteAsync("INSERT INTO orders VALUES(1,1)", cancellationToken: timeout.Token);
        await user.ExecuteAsync("INSERT INTO users VALUES(1,9)", cancellationToken: timeout.Token);
        await using (var other = await peer.CreateSessionAsync(timeout.Token))
        { await other.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: timeout.Token); }
        await using var transaction = await user.BeginTransactionAsync(timeout.Token);
        await user.ExecuteAsync("UPDATE users SET legacy=8", cancellationToken: timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

        // Act: the first migration holds orders and waits for users before queueing.
        var migration = Task.Run(async () => await database.SchemaProvisioner.ApplyAsync(target, timeout.Token, deferDataScalingSteps: true), timeout.Token);
        await waiting.Task.WaitAsync(timeout.Token);
        await peer.SchemaProvisioner.ApplyAsync(Peer(Target().Compile()), timeout.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(peer, timeout.Token);
        peer.Catalog.SchemaState!.ContentHash.ShouldBe(Peer(Target().Compile()).Hash);
        await Should.ThrowAsync<DatabaseException>(async () => await user.ExecuteAsync("UPDATE orders SET note=2", cancellationToken: timeout.Token));
        await transaction.RollbackAsync(timeout.Token);
        await migration.WaitAsync(timeout.Token);
        await WaitDoneAsync(database, timeout.Token);

        // Assert
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        database.OfflineError.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: re-pointed FKs reserve the old parent until atomic publication")]
    public async Task ApplySchemaAsync_RepointedReference_ShouldProtectOldAndNewParents()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("joint-fk", new());
        var database = await engine.CreateDatabaseAsync("typed", timeout.Token);
        CompiledSchemaTable Parent(string name) => new(name, name, [new("id", DatabaseType.Int32, false)], new("pk_" + name, ["id"]), [], []);
        CompiledSchemaTable Orders(string parent, DatabaseType type) => new("orders", "orders",
            [new("id", DatabaseType.Int32, false), new("cust_id", DatabaseType.Int32, true), new("note", type, true)],
            new("pk_orders", ["id"]), [], [new("fk_customer", CompiledSchemaConstraintKind.Reference, ["cust_id"], parent, ["id"])]);
        var source = Compiled(Parent("customers_v1"), Parent("customers"), Orders("customers_v1", DatabaseType.Int32));
        var target = Compiled(Parent("customers_v1"), Parent("customers"), Orders("customers", DatabaseType.Int64));
        await database.ApplySchemaAsync(source, timeout.Token);
        await using var user = await database.CreateSessionAsync(timeout.Token);
        await user.ExecuteAsync("INSERT INTO customers_v1 VALUES(1)", cancellationToken: timeout.Token);
        await user.ExecuteAsync("INSERT INTO customers VALUES(1)", cancellationToken: timeout.Token);
        await user.ExecuteAsync("INSERT INTO orders VALUES(1,1,9)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(target, timeout.Token, deferDataScalingSteps: true);
        await entered.Task.WaitAsync(timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        try
        {
            // Act
            var deleting = user.ExecuteAsync("DELETE FROM customers_v1", cancellationToken: timeout.Token).AsTask();
            await waiting.Task.WaitAsync(timeout.Token);
            deleting.IsCompleted.ShouldBeFalse();
            release.Set();
            await deleting;
            await WaitDoneAsync(database, timeout.Token);

            // Assert
            await Should.ThrowAsync<DatabaseException>(async () => await user.ExecuteAsync("DELETE FROM customers", cancellationToken: timeout.Token));
            (await RowsAsync(user, "SELECT cust_id FROM orders", timeout.Token)).Single()[0].ShouldBe(1);
        }
        finally { release.Set(); }
    }
}
