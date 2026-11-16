using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record DeferredBackfill(int Id, int Added);
    private sealed record DeferredCreated(int Id);

    private static SqlSchema DeferredDeclaration(bool target, bool backfill = false) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<OtherRow>("items", table =>
        {
            table.Key(row => row.Id);
            if (target) { table.Index(row => row.Id); table.Check("ck_positive", "Id > 0"); }
        });
        schema.Table<DeferredBackfill>("filled", table =>
        {
            table.Key(row => row.Id);
            if (target && backfill) { table.Column(row => row.Added); table.Default(row => row.Added, 7); }
        });
        if (target) { schema.Table<DeferredCreated>("created", table => table.Key(row => row.Id)); }
    });

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred: every discard commit image converges with no, matching, third and rollback declarations")]
    public async Task OpenAsync_DeferredOnlyDiscardBoundaries_ShouldKeepStoredSchemaAndReportDiscard()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        await using (var engine = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            var target = DeferredDeclaration(true).Compile();
            await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                [new("Joint/AddIndex", "items", "ix", 1), new("Joint/KeepOwner", "items", "items", 2)], 2), guard.Token);
            using var events = new Assimalign.Cohesion.Testing.EventSourceRecorder(SqlDatabaseEventSource.Log, System.Diagnostics.Tracing.EventLevel.Verbose);

            // Act
            recorder = storage.RecordCrashPoints();
            await database.SchemaProvisioner.DiscardUnpublishedIntentAsync(DeferredDeclaration(false).Compile(), guard.Token);
            recorder.Dispose();

            // Assert
            database.Provisioning!.FailureCode.ShouldBe("CS03402");
            events.Events.ShouldContain(entry => entry.EventId == 15 && Equals(entry.Payload![5], "CS03402"));
            events.Events.ShouldContain(entry => entry.EventId == 14 && Equals(entry.Payload![5], "Restarted"));
            events.Events.ShouldContain(entry => entry.EventId == 14 && Equals(entry.Payload![5], "Discarded"));
            events.Events.ShouldNotContain(entry => entry.EventId == 14 && Equals(entry.Payload![5], "Error"));
        }
        recorder.Count.ShouldBeGreaterThan(1);
        for (int point = 0; point < recorder.Count; point++)
        {
            for (int mode = 0; mode < 4; mode++)
            { await CheckDeferredCrashImageAsync(recorder.Open(point), false, mode, null, guard.Token, 0); }
        }
        _output.WriteLine($"Deferred discard storage images={recorder.Count}; reopens={recorder.Count * 4}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred: a valid pending target cannot hide an untouched unresolved domain from Build")]
    public async Task BuildAsync_DeferredOnlyUntouchedUnresolvedDomain_ShouldRefuseAndKeepOldSchema()
    {
        // Arrange: the ad-hoc table is outside the valid migration target.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var seedBuilder = Builder(storage);
        seedBuilder.Types.Add(SqlType.CreateDomain("native_value", SqlType.Integer));
        await using (var seed = await seedBuilder.AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("CREATE TABLE untouched(v native_value)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        int handoffs = 0;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
            { if (phase == "Handoff") { Interlocked.Increment(ref handoffs); } };
            return new RecordingWorker(engine);
        });

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await builder.AddDatabase(DeferredDeclaration(true)).BuildAsync(guard.Token));

        // Assert: validating a good target never replaces the live validation result.
        handoffs.ShouldBe(1);
        failure.InnerException.ShouldBeOfType<SqlEvaluationException>().Code.ShouldBe("CS02010");
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        current.Definitions.FindUnresolvedDomain(guard.Token).ShouldNotBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
        current.Catalog.PendingMigration.ShouldBeNull();
        current.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred: handoff exception, cancellation and OOM release every reservation")]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("oom")]
    public async Task ApplyAsync_DeferredOnlyHandoffFailure_ShouldReleaseLocksGateAndMetadata(string failureKind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "Handoff") { return; }
            if (failureKind == "oom") { throw new OutOfMemoryException("injected handoff"); }
            if (failureKind == "exception") { throw new InvalidOperationException("injected handoff"); }
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        // Act
        async Task Apply() => await database.SchemaProvisioner.ApplyAsync(DeferredDeclaration(true).Compile(), cancellation.Token, deferDataScalingSteps: true);
        if (failureKind == "oom") { await Should.ThrowAsync<OutOfMemoryException>(Apply); }
        else if (failureKind == "cancel") { await Should.ThrowAsync<OperationCanceledException>(Apply); }
        else { await Should.ThrowAsync<SqlSchemaMigrationException>(Apply); }

        // Assert
        database.SchemaProvisioner.Deferred.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        database.IsOffline.ShouldBeFalse();
        await writer.ExecuteAsync("INSERT INTO items VALUES(-1)", cancellationToken: guard.Token);
        await database.SchemaProvisioner.VerifyAsync(DeferredDeclaration(false).Compile(), guard.Token);
        await writer.ExecuteAsync("CREATE VIEW released_metadata AS SELECT Id FROM items", cancellationToken: guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred: every preparation failure preserves the old schema and rollback Build")]
    [InlineData("IntentDurable")]
    [InlineData("Handoff")]
    [InlineData("OwnersDurable")]
    [InlineData("TreesDurable")]
    [InlineData("BeforeTreeBuild")]
    [InlineData("AfterTreeBuild")]
    [InlineData("KeepPrepared")]
    [InlineData("Validated")]
    [InlineData("Publishing")]
    public async Task BuildAsync_DeferredOnlyStageFailure_ShouldKeepOldSchemaAndPermitRollback(string boundary)
    {
        // Arrange: the plan contains cheap table creation, an owner-keeping index/CHECK
        // and a required-column backfill, without any TYPE change.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1);", cancellationToken: guard.Token);
            await writer.ExecuteAsync("INSERT INTO filled VALUES(1);", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        int visits = 0;
        CrashCaptureSqlStorageStrategy? crash = null;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase != boundary) { return; }
                Interlocked.Increment(ref visits);
                crash = storage.CaptureDurableImages();
                throw new InvalidOperationException("injected joint deferred boundary");
            };
            return new RecordingWorker(engine);
        });

        // Act
        if (boundary is "IntentDurable" or "Handoff")
        {
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await builder.AddDatabase(DeferredDeclaration(true, true)).BuildAsync(guard.Token));
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        }
        else
        {
            await using var engine = await builder.AddDatabase(DeferredDeclaration(true, true)).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await WaitDoneAsync(database, guard.Token);
            database.Provisioning!.FailureCode.ShouldBe("CS03402");
            database.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
            database.Catalog.TryGetTable("dbo", "created", out _).ShouldBeFalse();
            database.Catalog.PendingMigration.ShouldBeNull();
            await DrainReclamationAsync(database, guard.Token);
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(-1)", cancellationToken: guard.Token);
        }

        // Assert: old and matching application Builds both open the interrupted image.
        visits.ShouldBe(1);
        await using (var rollback = await Builder(crash!.CaptureDurableImages()).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await rollback.OpenDatabaseAsync("typed", guard.Token);
            database.IsOffline.ShouldBeFalse();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Provisioning.ShouldBeNull();
            await DrainReclamationAsync(database, guard.Token);
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        }
        await using var retry = await Builder(crash!.CaptureDurableImages()).AddDatabase(DeferredDeclaration(true, true)).BuildAsync(guard.Token);
        var resumed = await retry.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(resumed, guard.Token);
        resumed.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(true, true).Compile().Hash);
        resumed.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        resumed.Provisioning.ShouldBeNull();
        await using var reader = await resumed.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Added FROM filled", guard.Token)).Single()[0].ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred: every storage boundary converges to exactly the old or complete new catalog")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_DeferredOnlyCrashImages_ShouldConvergeAndReclaimExactlyOnce(bool backfill)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var storage = new CrashCaptureSqlStorageStrategy();
        var phases = new Dictionary<string, CrashCaptureSqlStorageStrategy>();
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        ulong heap;
        await using (var engine = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            heap = Table(database).ObjectId;
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1),(2)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("INSERT INTO filled VALUES(1)", cancellationToken: guard.Token);
            database.SchemaProvisioner.JointStage = phase => phases[phase] = storage.CaptureDurableImages();

            // Act: include intent save, every copy/tree write and publication/discard.
            recorder = storage.RecordCrashPoints();
            await database.SchemaProvisioner.ApplyAsync(DeferredDeclaration(true, backfill).Compile(), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
            recorder.Dispose();
        }

        // Assert
        foreach (string phase in new[] { "IntentDurable", "Handoff", "OwnersDurable", "TreesDurable", "BeforeTreeBuild", "AfterTreeBuild", "Validated", "Publishing", "Published" })
        { phases.ShouldContainKey(phase); }
        int old = 0, changed = 0, images = 0;
        foreach (var image in phases.Values.Concat(Enumerable.Range(0, recorder.Count).Select(recorder.Open)))
        {
            images++;
            for (int mode = 0; mode < 4; mode++)
            {
                bool applied = await CheckDeferredCrashImageAsync(image.CaptureDurableImages(), backfill, mode, heap, guard.Token);
                if (mode == 0) { if (applied) { changed++; } else { old++; } }
            }
        }
        old.ShouldBeGreaterThan(0);
        changed.ShouldBeGreaterThan(0);
        _output.WriteLine($"Deferred crash images: backfill={backfill}; old={old}; new={changed}; images={images}; reopens={images * 4}");
    }

    private sealed record DeferredThird(int Id);
    private static SqlSchema ThirdDeferredDeclaration() => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<OtherRow>("items", table => { table.Key(row => row.Id); table.Index(row => row.Id); table.Check("ck_third", "Id >= 0"); });
        schema.Table<DeferredBackfill>("filled", table => table.Key(row => row.Id));
        schema.Table<DeferredThird>("third", table => table.Key(row => row.Id));
    });
    private static SqlCompiledSchema ThirdDeferredTarget() => ThirdDeferredDeclaration().Compile();

    private static async Task<bool> CheckDeferredCrashImageAsync(CrashCaptureSqlStorageStrategy image, bool backfill,
        int mode, ulong? heap, CancellationToken token, int expectedRows = 2)
    {
        var builder = Builder(image);
        if (mode == 1) { builder.AddDatabase(DeferredDeclaration(true, backfill)); }
        if (mode == 2) { builder.AddDatabase(ThirdDeferredDeclaration()); }
        if (mode == 3) { builder.AddDatabase(DeferredDeclaration(false)); }
        int thirdJobs = 0;
        if (mode == 2)
        {
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = current => current.SchemaProvisioner.JointStage = phase =>
                { if (phase == "IntentDurable") { thirdJobs++; } };
                return new RecordingWorker(engine);
            });
        }
        await using var recovered = await builder.BuildAsync(token);
        var database = await recovered.OpenDatabaseAsync("typed", token);
        await WaitDoneAsync(database, token);
        if (mode == 2 && expectedRows > 0) { thirdJobs.ShouldBe(1); }
        bool applied = database.Catalog.SchemaState!.ContentHash == DeferredDeclaration(true, backfill).Compile().Hash;
        database.Catalog.SchemaState.ContentHash.ShouldBe(mode == 2 ? ThirdDeferredTarget().Hash : DeferredDeclaration(mode == 1 || applied, backfill).Compile().Hash);
        if (heap is { } owner) { Table(database).ObjectId.ShouldBe(owner); }
        Table(database).Constraints.Count.ShouldBe(applied || mode == 2 ? 1 : 0);
        database.Catalog.GetIndexes(Table(database).ObjectId).Count.ShouldBe(1 + (applied || mode == 2 ? 1 : 0));
        database.Catalog.TryGetTable("dbo", "created", out _).ShouldBe(applied);
        database.Catalog.TryGetTable("dbo", "third", out _).ShouldBe(mode == 2);
        database.Catalog.TryGetTable("dbo", "filled", out var filled).ShouldBeTrue();
        (filled.FindColumn("Added") is not null).ShouldBe(applied && backfill);
        database.Catalog.GetIndexes(Table(database).ObjectId).Single(index => index.IsPrimaryKey).Name.ShouldBe("PrimaryKey_items_Id_0");
        database.Catalog.GetIndexes(filled.ObjectId).Single(index => index.IsPrimaryKey).Name.ShouldBe("PrimaryKey_filled_Id_0");
        database.Catalog.PendingMigration.ShouldBeNull();
        if (mode == 3 && applied)
        {
            database.Provisioning!.FailureCode.ShouldBe("CS03405");
            database.Provisioning.Detail!.ShouldContain("DropTable 'created'", Case.Sensitive);
        }
        else { database.Provisioning.ShouldBeNull(); }
        database.IsOffline.ShouldBeFalse();
        await using var reader = await database.CreateSessionAsync(token);
        var rows = await RowsAsync(reader, "SELECT Id FROM items ORDER BY Id", token);
        rows.Select(row => row[0]).ShouldBe(Enumerable.Range(1, expectedRows).Select(value => (object?)value));
        if (applied && backfill) { (await RowsAsync(reader, "SELECT Added FROM filled", token)).Single()[0].ShouldBe(7); }
        await DrainReclamationAsync(database, token);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        var liveHeaps = database.Catalog.Tables.Select(table => table.ObjectId).ToHashSet();
        var liveTrees = database.IndexManager.ExportRegistrations().Select(database.IndexManager.PageOwner).ToHashSet();
        for (long page = 1; page < database.DataStorage.PageManager.PageCount; page++)
        {
            if (!database.DataStorage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
            using var handle = database.DataStorage.PageManager.GetPage((PageId)page);
            if (handle.Page.Type == PageType.Index) { liveTrees.ShouldContain(handle.Page.OwnerId); }
            else if (handle.Page.OwnerId != 0) { liveHeaps.ShouldContain(handle.Page.OwnerId); }
        }
        long reclaimed = database.TableReclamation.ReclaimedPages;
        await DrainReclamationAsync(database, token);
        database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed);
        if (heap is { } retained) { database.DataStorage.HasOwnerPages(retained).ShouldBeTrue(); }
        return applied;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred: metadata floor, measured admission and publication watchdog preserve the old schema")]
    [InlineData("Planning")]
    [InlineData("Handoff")]
    [InlineData("AfterTreeBuild")]
    public async Task BuildAsync_DeferredOnlySpaceRefusal_ShouldRetainOldObjects(string boundary)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        ulong heap = Table(database).ObjectId;
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == boundary) { storage.MigrationAvailableBytes = 0; } };

        // Act
        if (boundary == "Planning")
        {
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.SchemaProvisioner.ApplyAsync(DeferredDeclaration(true).Compile(), guard.Token, deferDataScalingSteps: true));
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        }
        else
        {
            await database.SchemaProvisioner.ApplyAsync(DeferredDeclaration(true).Compile(), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
        }

        // Assert
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        database.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        Table(database).ObjectId.ShouldBe(heap);
        database.Catalog.GetIndexes(heap).ShouldHaveSingleItem().IsPrimaryKey.ShouldBeTrue();
        database.IsOffline.ShouldBeFalse();
        storage.MigrationAvailableBytes = long.MaxValue;
        await DrainReclamationAsync(database, guard.Token);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await writer.ExecuteAsync("INSERT INTO items VALUES(-1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = null;
        await database.SchemaProvisioner.VerifyAsync(DeferredDeclaration(false).Compile(), guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred: waiting writes cancel until counters prove admission, then enforce the published CHECK")]
    public async Task ApplyAsync_DeferredOnlyAdmissionRace_ShouldReleaseCancelledWaitersAndEnforceNewConstraint()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlCompiledSchema Declaration(bool target) => Compiled(DeferredDeclaration(target).Compile().Tables.Select(table => table.Name == "items"
            ? new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey, table.Indexes,
                table.Constraints.Append(new CompiledSchemaConstraint("fk_customer", CompiledSchemaConstraintKind.Reference, ["Id"], "customers", ["id"])).ToArray()) : table)
            .Append(new CompiledSchemaTable("customers", "customers", [new("id", Assimalign.Cohesion.Database.Types.DatabaseType.Int32, false),
                new("note", Assimalign.Cohesion.Database.Types.DatabaseType.Int32, true)], new("pk", ["id"]), [], [])).ToArray());
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Declaration(false), guard.Token);
        await using var seed = await database.CreateSessionAsync(guard.Token);
        await seed.ExecuteAsync("INSERT INTO customers VALUES(1,7)", cancellationToken: guard.Token);
        await seed.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == "AfterTreeBuild") { ready.TrySetResult(); release.Wait(guard.Token); } };
        int waits = 0, cancellations = 0, neighbourReads = 0, neighbourUpdates = 0, neighbourInserts = 0;
        TaskCompletionSource? waiting = null;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value =>
        { if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); waiting?.TrySetResult(); } };

        // Act
        await database.SchemaProvisioner.ApplyAsync(Declaration(true), guard.Token, deferDataScalingSteps: true);
        await ready.Task.WaitAsync(guard.Token);
        try
        {
            var elapsed = Stopwatch.StartNew();
            do
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
                await using var writer = await database.CreateSessionAsync(guard.Token);
                waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var write = writer.ExecuteAsync("INSERT INTO items VALUES(-1)", cancellationToken: cancellation.Token).AsTask();
                await waiting.Task.WaitAsync(guard.Token);
                cancellation.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await write);
                cancellations++;
                (await RowsAsync(seed, "SELECT note FROM customers WHERE id=1", guard.Token)).ShouldHaveSingleItem();
                neighbourReads++;
                await seed.ExecuteAsync("UPDATE customers SET note=8 WHERE id=1", cancellationToken: guard.Token);
                neighbourUpdates++;
                await seed.ExecuteAsync($"INSERT INTO customers VALUES({cancellations + 1},7)", cancellationToken: guard.Token);
                neighbourInserts++;
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || cancellations < 8 || Volatile.Read(ref waits) < 8);
        }
        finally { release.Set(); await WaitDoneAsync(database, guard.Token); }

        // Assert: cancellation/disposal released user grants; completion released the
        // migration's table reservation, apply gate and metadata lease.
        waits.ShouldBeGreaterThanOrEqualTo(8);
        cancellations.ShouldBeGreaterThanOrEqualTo(8);
        neighbourReads.ShouldBeGreaterThanOrEqualTo(8);
        neighbourUpdates.ShouldBeGreaterThanOrEqualTo(8);
        neighbourInserts.ShouldBeGreaterThanOrEqualTo(8);
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await seed.ExecuteAsync("INSERT INTO items VALUES(-1)", cancellationToken: guard.Token));
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03038");
        await seed.ExecuteAsync("INSERT INTO items VALUES(2)", cancellationToken: guard.Token);
        await database.SchemaProvisioner.VerifyAsync(Declaration(true), guard.Token);
        await seed.ExecuteAsync("CREATE VIEW after_migration AS SELECT Id FROM items", cancellationToken: guard.Token);
        _output.WriteLine($"Deferred admission stress: lock waits={waits}; canceled writers={cancellations}; neighbour reads={neighbourReads}; updates={neighbourUpdates}; inserts={neighbourInserts}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred: index-only page writes stay within ten percent of the retained 49a builder")]
    public async Task ApplyAsync_DeferredOnlyIndexCost_ShouldStayWithinTenPercentOfPerStepBuilder()
    {
        // Arrange: the retained DDL executor with progress enabled is exactly 49a's
        // bounded index builder. Compare identical seeded images and flush both ends.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        SqlSchema Declaration(bool index) => SqlSchema.Create("typed", schema => schema.Table<OtherRow>("items", table =>
        { table.Key(row => row.Id); if (index) { table.Index(row => row.Id); } }));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(Declaration(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await SeedShuffledIndexRowsAsync(database, 32768, guard.Token);
            database.Coordinator.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        var writes = new long[2];
        var rewrites = new long[2];
        var decodes = new long[2];
        for (int mode = 0; mode < 2; mode++)
        {
            var image = storage.CaptureDurableImages();
            await using var engine = await Builder(image).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            var heap = Table(database);
            database.Coordinator.Checkpoint();
            database.CatalogStorage.Checkpoint();
            long before = image.PageWrites("typed");
            int measured = mode;
            SqlPlanExecutor.RowDecoded = _ => decodes[measured]++;
            SqlPlanExecutor.RewriteStage = (phase, rows) => { if (phase == "Copied") { rewrites[measured] = rows; } };
            try
            {
                // Act
                if (mode == 0)
                {
                    var source = Declaration(false).Compile();
                    var target = Declaration(true).Compile();
                    var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(source, target), source);
                    await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                        [new("AddIndex", "items", script.Steps.Single().Operation.ObjectName, 1)], 1), guard.Token);
                    var locks = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
                    try
                    {
                        await using var session = database.CreateSchemaSession("typed", guard.Token, locks.Sequence);
                        session.ProvisioningProgress = (_, _) => { };
                        await session.ExecuteAsync(script.Steps.Single().Request, guard.Token);
                        await database.Catalog.SaveSchemaStateAsync(new(target.Hash, target.CanonicalDocument, database.Catalog.DdlGeneration), guard.Token);
                    }
                    finally { await database.Coordinator.RollbackAsync(locks, CancellationToken.None); }
                }
                else
                {
                    await database.SchemaProvisioner.ApplyAsync(Declaration(true).Compile(), guard.Token, deferDataScalingSteps: true);
                    await WaitDoneAsync(database, guard.Token);
                    database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
                }
                database.Coordinator.Checkpoint();
                database.CatalogStorage.Checkpoint();
                writes[mode] = image.PageWrites("typed") - before;

                // Assert
                Table(database).ObjectId.ShouldBe(heap.ObjectId);
                rewrites[mode].ShouldBe(0);
                decodes[mode].ShouldBe(65536);
                database.Catalog.GetIndexes(heap.ObjectId).Count.ShouldBe(2);
            }
            finally { SqlPlanExecutor.RowDecoded = null; SqlPlanExecutor.RewriteStage = null; }
        }
        writes[0].ShouldBeGreaterThan(0);
        writes[1].ShouldBeLessThanOrEqualTo(writes[0] * 110 / 100);
        _output.WriteLine($"32768-row index-only cost: 49a pages written={writes[0]}, joint pages written={writes[1]}; rows rewritten={rewrites[0]}/{rewrites[1]}; rows decoded={decodes[0]}/{decodes[1]}");
    }
}
