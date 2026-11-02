using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private static long IndexOwnerPages(SqlDatabase database, ulong owner)
    {
        long count = 0;
        for (long page = 1; page < database.DataStorage.PageManager.PageCount; page++)
        {
            if (!database.DataStorage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
            using var handle = database.DataStorage.PageManager.GetPage((PageId)page);
            if (handle.Page.Type == PageType.Index && handle.Page.OwnerId == owner) { count++; }
        }
        return count;
    }
    private sealed record OwnerNarrow(int Value);
    private sealed record OwnerWide(long Value);

    private static SqlSchema OwnerSchema(bool changed) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (changed) { schema.Table<OwnerWide>("rewrite", table => table.Column(row => row.Value)); }
        else { schema.Table<OwnerNarrow>("rewrite", table => table.Column(row => row.Value)); }
        schema.Table<OtherRow>("items", table =>
        {
            table.Column(row => row.Id);
            if (changed) { table.Index(row => row.Id); }
        });
    });

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: empty Build, forced inline, deferred and Apply use the same durable classification")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BuildAsync_KeptOwnerEntryPaths_ShouldKeepHeap(int mode)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong heap;
        await using (var seed = await Builder(storage).AddDatabase(OwnerSchema(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            heap = Table(database).ObjectId;
            if (mode != 0)
            {
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(7)", cancellationToken: guard.Token);
            }
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        if (mode != 3) { builder.AddDatabase("typed", database => { database.Schema(OwnerSchema(true)); database.RunMigrationsInline = mode == 1; }); }

        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        if (mode == 3) { await current.ApplySchemaAsync(OwnerSchema(true).Compile(), guard.Token); }
        await WaitDoneAsync(current, guard.Token);

        // Assert
        Table(current).ObjectId.ShouldBe(heap);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(OwnerSchema(true).Compile().Hash);
        current.Catalog.GetIndexes(heap).ShouldHaveSingleItem();
        current.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: interrupted matching Build rebuilds trees and reclaims abandoned owners")]
    public async Task BuildAsync_KeptOwnerInterrupted_ShouldResumeOnlyMatchingTarget()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy? crash = null;
        ulong heap;
        await using (var engine = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(OwnerSchema(false).Compile(), guard.Token);
            heap = Table(database).ObjectId;
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(7)", cancellationToken: guard.Token);
            database.SchemaProvisioner.JointStage = stage =>
            {
                if (stage != "AfterTreeBuild") { return; }
                database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == "Joint/KeepOwner" && step.Table == "items");
                crash = storage.CaptureDurableImages();
                throw new InvalidOperationException("crash after tree build");
            };
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(OwnerSchema(true).Compile(), guard.Token));
        }

        // Act
        await using var resumed = await Builder(crash!).AddDatabase(OwnerSchema(true)).BuildAsync(guard.Token);
        var current = await resumed.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(current, guard.Token);
        await DrainReclamationAsync(current, guard.Token);

        // Assert
        Table(current).ObjectId.ShouldBe(heap);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(OwnerSchema(true).Compile().Hash);
        current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Id FROM items WHERE Id=7", guard.Token)).Single()[0].ShouldBe(7);
    }

    private static SqlCompiledSchema OwnerDeclaration(bool changed, IReadOnlyList<CompiledSchemaIndex>? indexes = null,
        IReadOnlyList<CompiledSchemaConstraint>? constraints = null, bool nullable = true)
        => Compiled(new("rewrite", "rewrite", [new("v", changed ? DatabaseType.Int64 : DatabaseType.Int32, false)], null, [], []),
            new("items", "items", [new("v", DatabaseType.Int32, nullable), new("w", DatabaseType.Int32, true)], null,
                indexes ?? (changed ? [new("ix_v", ["v"], false)] : []), constraints ?? []));

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: 32768 indexed rows write only their tree with a whole-table negative control")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_IndexOnlyTable_ShouldKeepHeapAndWriteOnlyTreePages(bool keepsOwner)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declaration(bool changed) => Compiled(OwnerDeclaration(changed).Tables.Select(table => table.Name != "items"
            ? table : new CompiledSchemaTable(table.Name, table.RowType, [table.Columns[0]], table.PrimaryKey, table.Indexes, table.Constraints)).ToArray());
        await database.ApplySchemaAsync(Declaration(false), guard.Token);
        SeedRaw(database, [7], 32768);
        await using (var writer = await database.CreateSessionAsync(guard.Token))
        { await writer.ExecuteAsync("INSERT INTO rewrite VALUES(1)", cancellationToken: guard.Token); }
        var heap = Table(database);
        var pages = database.DataStorage.GetOwnerPages(heap.ObjectId).ToArray();
        long allocations = database.TableReclamation.CaptureAllocationCount();
        long rewritten = 0;
        long copiedPrevious = 0;
        long decoded = 0, buildDecoded = 0;
        bool building = false;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "BeforeTreeBuild") { building = true; }
            if (stage == "AfterTreeBuild") { building = false; }
        };
        SqlPlanExecutor.RowDecoded = _ => { if (building) { buildDecoded++; } else { decoded++; } };
        if (!keepsOwner)
        {
            // Negative control: run the old executor's all-rewrite classification
            // through the exact same physical copy path and work counters.
            database.SchemaProvisioner.BeforeDeferredStep = (_, _) =>
            {
                var tables = database.SchemaProvisioner.Deferred!.JointPlan!.Tables;
                for (int index = 0; index < tables.Length; index++) { tables[index] = tables[index] with { KeepsOwner = false }; }
            };
        }
        SqlPlanExecutor.RewriteStage = (stage, count) =>
        {
            if (stage != "Copied") { return; }
            rewritten += count <= copiedPrevious ? count : count - copiedPrevious;
            copiedPrevious = count;
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(Declaration(true), guard.Token);

            // Assert
            if (keepsOwner)
            {
                Table(database).ObjectId.ShouldBe(heap.ObjectId);
                database.DataStorage.GetOwnerPages(heap.ObjectId).ShouldBe(pages);
            }
            else { Table(database).ObjectId.ShouldNotBe(heap.ObjectId); }
            rewritten.ShouldBe(keepsOwner ? 1 : 32769);
            database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
            long allocated = database.TableReclamation.CaptureAllocationCount() - allocations;
            var registration = database.IndexManager.ExportRegistrations().Single();
            long treePages = IndexOwnerPages(database, database.IndexManager.PageOwner(registration));
            allocated.ShouldBe(treePages + 1 + (keepsOwner ? 0 : pages.LongLength));
            allocated.ShouldBeLessThan(keepsOwner ? 150 : 300);
            allocated.ShouldBeGreaterThan(keepsOwner ? 100 : 250);
            database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeLessThan(3 * allocated * 8192);
            _output.WriteLine($"32768 rows: keepsOwner={keepsOwner}; indexed-table rewrites={rewritten - 1}; total rewrites={rewritten}; validation/copy decodes={decoded}; index-build decodes={buildDecoded}; allocated pages={allocated}; tree pages={treePages}; R={database.SchemaProvisioner.JointSpaceReplacementBytes}");
            decoded.ShouldBe(keepsOwner ? 1 : 32769);
            buildDecoded.ShouldBe(keepsOwner ? 65536 : 0);
            await using var reader = await database.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE v=7", guard.Token)).Single()[0].ShouldBe(32768L);
        }
        finally { SqlPlanExecutor.RewriteStage = null; SqlPlanExecutor.RowDecoded = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: CHECK, NOT NULL, UNIQUE and FK refusals preserve the old schema online")]
    [InlineData("check")]
    [InlineData("notnull")]
    [InlineData("unique")]
    [InlineData("foreignkey")]
    public async Task ApplyAsync_KeptConstraintFailure_ShouldKeepOldOnline(string kind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = OwnerDeclaration(false);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO rewrite VALUES(1)", cancellationToken: guard.Token);
        await writer.ExecuteAsync(kind == "notnull" ? "INSERT INTO items VALUES(NULL,1)" : "INSERT INTO items VALUES(7,1),(7,2)", cancellationToken: guard.Token);
        var old = Table(database);
        CompiledSchemaConstraint[] constraints = kind switch
        {
            "check" => [new("ck", CompiledSchemaConstraintKind.Check, [], null, [], new("v < 3"))],
            "foreignkey" => [new("fk", CompiledSchemaConstraintKind.Reference, ["v"], "parent", ["v"])],
            _ => [],
        };
        var target = OwnerDeclaration(true, indexes: kind == "unique" ? [new("uq", ["v"], true)] : null,
            constraints: kind == "foreignkey" ? [] : constraints, nullable: kind != "notnull");
        if (kind == "foreignkey")
        {
            target = Compiled(target.Tables.Select(table => table.Name != "items" ? table : new CompiledSchemaTable(table.Name,
                table.RowType, table.Columns, table.PrimaryKey, table.Indexes, constraints))
                .Append(new("parent", "parent", [new("v", DatabaseType.Int32, false)], new("pk", ["v"]), [], [])).ToArray());
        }

        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));

        // Assert
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        database.OfflineError.ShouldBeNull();
        Table(database).ShouldBeSameAs(old);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        await writer.ExecuteAsync("INSERT INTO items VALUES(9,3)", cancellationToken: guard.Token);
        await writer.DisposeAsync();
        await DrainReclamationAsync(database, guard.Token);
        database.DataStorage.HasOwnerPages(old.ObjectId).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: failures around tree builds reclaim only unpublished owners")]
    [InlineData("BeforeTreeBuild", false)]
    [InlineData("AfterTreeBuild", false)]
    [InlineData("Publishing", false)]
    [InlineData("AfterTreeBuild", true)]
    public async Task ApplyAsync_KeptTreeFault_ShouldPreservePublishedHeapAndIndex(string boundary, bool cancel)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = OwnerDeclaration(false, [new("old", ["w"], false)]);
        var target = OwnerDeclaration(true, [new("old", ["w"], false), new("ix_v", ["v"], false)]);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        var heap = Table(database).ObjectId;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != boundary) { return; }
            if (cancel) { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("injected tree boundary");
        };

        // Act
        await Should.ThrowAsync<Exception>(async () => await database.ApplySchemaAsync(target, stop.Token));

        // Assert
        database.OfflineError.ShouldBeNull();
        Table(database).ObjectId.ShouldBe(heap);
        database.Catalog.GetIndexes(heap).ShouldHaveSingleItem().Name.ShouldBe("old");
        (await RowsAsync(writer, "SELECT v FROM items WHERE w=8", guard.Token)).Single()[0].ShouldBe(7);
        await writer.DisposeAsync();
        await DrainReclamationAsync(database, guard.Token);
        database.DataStorage.HasOwnerPages(heap).ShouldBeTrue();
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(target, guard.Token);
        Table(database).ObjectId.ShouldBe(heap);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: every durable tree/publication image converges and preserves the kept heap")]
    public async Task OpenAsync_KeptTreeCrashImages_ShouldConvergeAtEveryBoundary()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var storage = new CrashCaptureSqlStorageStrategy();
        var stages = new Dictionary<string, CrashCaptureSqlStorageStrategy>();
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        var source = CrashOwnerDeclaration(1).Compile();
        var targetDeclaration = CrashOwnerDeclaration(2);
        var target = targetDeclaration.Compile();
        ulong heap;
        await using (var engine = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(CrashOwnerDeclaration(0).Compile(), guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            heap = Table(database).ObjectId;
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(7,8,11),(9,10,12)", cancellationToken: guard.Token);
            database.SchemaProvisioner.JointStage = stage => stages[stage] = storage.CaptureDurableImages();

            // Act
            recorder = storage.RecordCrashPoints();
            await database.ApplySchemaAsync(target, guard.Token);
            recorder.Dispose();
        }

        // Assert
        foreach (string stage in new[] { "BeforeTreeBuild", "AfterTreeBuild", "Publishing", "Published" }) { stages.ShouldContainKey(stage); }
        int old = 0, changed = 0;
        foreach (var image in stages.Values.Concat(Enumerable.Range(0, recorder.Count).Select(recorder.Open)))
        {
            foreach (bool resume in new[] { false, true })
            {
                var builder = Builder(image.CaptureDurableImages());
                if (resume) { builder.AddDatabase("typed", db => db.Schema(targetDeclaration)); }
                await using var reopened = await builder.BuildAsync(guard.Token);
                var database = await reopened.OpenDatabaseAsync("typed", guard.Token);
                await WaitDoneAsync(database, guard.Token);
                bool applied = database.Catalog.SchemaState!.ContentHash == target.Hash;
                if (resume) { applied.ShouldBeTrue(); }
                if (applied) { changed++; } else { old++; }
                database.Catalog.SchemaState.ContentHash.ShouldBe(applied ? target.Hash : source.Hash);
                Table(database).ObjectId.ShouldBe(heap);
                database.Catalog.GetIndexes(heap).Count.ShouldBe(applied ? 2 : 1);
                await using (var reader = await database.CreateSessionAsync(guard.Token))
                {
                    (await RowsAsync(reader, "SELECT v FROM items WHERE w=8", guard.Token)).Single()[0].ShouldBe(7);
                    (await RowsAsync(reader, "SELECT w FROM items WHERE v=9", guard.Token)).Single()[0].ShouldBe(10);
                }
                await DrainReclamationAsync(database, guard.Token);
                database.DataStorage.HasOwnerPages(heap).ShouldBeTrue();
                database.Catalog.PendingMigration.ShouldBeNull();
                var liveTreeOwners = database.IndexManager.ExportRegistrations().Select(database.IndexManager.PageOwner).ToHashSet();
                for (long page = 1; page < database.DataStorage.PageManager.PageCount; page++)
                {
                    if (!database.DataStorage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
                    using var handle = database.DataStorage.PageManager.GetPage((PageId)page);
                    if (handle.Page.Type == PageType.Index) { liveTreeOwners.ShouldContain(handle.Page.OwnerId); }
                }
            }
        }
        old.ShouldBeGreaterThan(0);
        changed.ShouldBeGreaterThan(0);
        _output.WriteLine($"Owner-keeping crash images: old={old}; new={changed}; total={old + changed}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: waiting writes cancel repeatedly then observe the published CHECK")]
    public async Task ApplyAsync_KeptOwnerAdmissionRace_ShouldHoldXThroughPublication()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        { if (stage == "AfterTreeBuild") { ready.TrySetResult(); release.Wait(guard.Token); } };
        var target = OwnerDeclaration(true, constraints: [new("ck", CompiledSchemaConstraintKind.Check, [], null, [], new("v < 3"))]);
        var applying = Task.Run(async () => await database.ApplySchemaAsync(target, guard.Token), guard.Token);
        await ready.Task.WaitAsync(guard.Token);
        int waits = 0, cancellations = 0;
        TaskCompletionSource? waiting = null;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); waiting?.TrySetResult(); } };
        try
        {
            // Act: minimum exercise interval and recorded admissions, bounded by guard.
            var elapsed = Stopwatch.StartNew();
            do
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
                await using var writer = await database.CreateSessionAsync(guard.Token);
                waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var write = writer.ExecuteAsync("INSERT INTO items VALUES(9,1)", cancellationToken: cancel.Token).AsTask();
                await waiting.Task.WaitAsync(guard.Token);
                cancel.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await write);
                cancellations++;
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || cancellations < 8 || Volatile.Read(ref waits) < 8);
            release.Set();
            await applying;

            // Assert
            waits.ShouldBeGreaterThanOrEqualTo(8);
            cancellations.ShouldBeGreaterThanOrEqualTo(8);
            await using var after = await database.CreateSessionAsync(guard.Token);
            await Should.ThrowAsync<DatabaseException>(async () => await after.ExecuteAsync("INSERT INTO items VALUES(9,1)", cancellationToken: guard.Token));
            await after.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
        }
        finally { release.Set(); await applying; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: later per-step DROP INDEX and TRUNCATE retire the separate tree owner")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_KeptTreeLaterRetirement_ShouldReclaimSeparateOwner(bool truncate)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        SeedRaw(database, [7, 8], 128);
        await database.ApplySchemaAsync(OwnerDeclaration(true), guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong treeOwner = database.IndexManager.PageOwner(registration);
        treeOwner.ShouldNotBe(Table(database).ObjectId);
        await DrainReclamationAsync(database, guard.Token);
        long reclaimed = database.TableReclamation.ReclaimedPages;

        // Act
        if (truncate)
        {
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("TRUNCATE items", cancellationToken: guard.Token);
        }
        else { await database.ApplySchemaAsync(OwnerDeclaration(true, []), guard.Token); }
        await DrainReclamationAsync(database, guard.Token);

        // Assert
        database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(reclaimed);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(truncate ? 0L : 128L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: reopened trees split with their own tag and repeated same-name replacement reclaims only the old tree")]
    public async Task ApplyAsync_ReopenedTreeSplitsAndReplacement_ShouldPreservePhysicalOwnership()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong heap;
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
            heap = Table(database).ObjectId;
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
            await database.ApplySchemaAsync(OwnerDeclaration(true), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        var old = current.IndexManager.ExportRegistrations().Single();
        ulong oldOwner = current.IndexManager.PageOwner(old);

        // Act
        await using (var writer = await current.CreateSessionAsync(guard.Token))
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(1, 1024).Select(value => $"({value},{value * 2})")), cancellationToken: guard.Token);
        }
        IndexOwnerPages(current, oldOwner).ShouldBeGreaterThan(1);
        await current.ApplySchemaAsync(OwnerDeclaration(false, [new("ix_v", ["w"], false)]), guard.Token);
        await DrainReclamationAsync(current, guard.Token);

        // Assert
        Table(current).ObjectId.ShouldBe(heap);
        IndexOwnerPages(current, oldOwner).ShouldBe(0);
        var replacement = current.IndexManager.ExportRegistrations().Single();
        current.IndexManager.PageOwner(replacement).ShouldNotBe(oldOwner);
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var final = await reopened.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await final.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v FROM items WHERE w=2048", guard.Token)).Single()[0].ShouldBe(1024);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(1025L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: successful NOT NULL preserves frozen tails and dropped physical ordinals")]
    public async Task ApplyAsync_KeptNotNullWithMissingTail_ShouldPreservePhysicalLayout()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = OwnerDeclaration(false);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        SqlCompiledSchema Tail(bool changed, bool drop, bool nullable) => Compiled(OwnerDeclaration(changed, []).Tables.Select(table => table.Name != "items"
            ? table : new CompiledSchemaTable(table.Name, table.RowType,
                table.Columns.Where(column => !drop || column.Name != "w").Append(new("tail", DatabaseType.Int32, nullable, DefaultLiteral: "11")).ToArray(),
                table.PrimaryKey, [], [])).ToArray());
        await database.ApplySchemaAsync(Tail(false, false, true), guard.Token);
        await database.ApplySchemaAsync(Tail(false, true, true), guard.Token);
        var before = Table(database);
        before.DroppedColumnOrdinals.ShouldNotBeEmpty();

        // Act
        await database.ApplySchemaAsync(Tail(true, true, false), guard.Token);

        // Assert
        var after = Table(database);
        after.ObjectId.ShouldBe(before.ObjectId);
        after.DroppedColumnOrdinals.ShouldBe(before.DroppedColumnOrdinals);
        after.FindColumn("tail")!.MissingValueLiteral.ShouldBe("11");
        after.FindColumn("tail")!.IsNullable.ShouldBeFalse();
        (await RowsAsync(writer, "SELECT tail FROM items", guard.Token)).Single()[0].ShouldBe(11);
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(9,NULL)", cancellationToken: guard.Token));
        await writer.ExecuteAsync("INSERT INTO items(v) VALUES(9)", cancellationToken: guard.Token);
        (await RowsAsync(writer, "SELECT tail FROM items WHERE v=9", guard.Token)).Single()[0].ShouldBe(11);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: broadening a sparse partial index admits the complete new tree")]
    public async Task ApplyAsync_KeptPartialIndexBroadening_ShouldChargeFreshTree()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        SeedRaw(database, [7, 8], 2048);
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("ix_v", ["v"], false, Predicate: "v < 0")]), guard.Token);
        ulong heap = Table(database).ObjectId;

        // Act
        await database.ApplySchemaAsync(OwnerDeclaration(false, [new("ix_v", ["v"], false)]), guard.Token);

        // Assert
        Table(database).ObjectId.ShouldBe(heap);
        var registration = database.IndexManager.ExportRegistrations().Single();
        long pages = IndexOwnerPages(database, database.IndexManager.PageOwner(registration));
        pages.ShouldBeGreaterThan(1);
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeGreaterThanOrEqualTo(pages * 8192);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE v=7", guard.Token)).Single()[0].ShouldBe(2048L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: unique writes retain logical key locks without taking physical-owner key locks")]
    public async Task ExecuteAsync_KeptUniqueTree_ShouldLockOnlyLogicalKeys()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("uq", ["v"], true)]), guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong physical = database.IndexManager.PageOwner(registration);
        ulong logical = Table(database).ObjectId;
        physical.ShouldNotBe(logical);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await using var transaction = await writer.BeginTransactionAsync(guard.Token);
        var probe = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        try
        {
            // Act
            await writer.ExecuteAsync("INSERT INTO items VALUES(8,1)", cancellationToken: guard.Token);
            ulong hash = IndexKey.From(new DatabaseKeyWriter().AppendInt32(8)).Hash();

            // Assert
            database.Coordinator.LockManager.TryAcquire(probe.Sequence, LockResource.Entry(logical, hash), LockMode.Exclusive).ShouldBeFalse();
            database.Coordinator.LockManager.TryAcquire(probe.Sequence, LockResource.Entry(physical, hash), LockMode.Exclusive).ShouldBeTrue();
        }
        finally { await database.Coordinator.RollbackAsync(probe, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: old snapshots join a kept heap with the old rewritten heap")]
    public async Task ApplyAsync_KeptAndRewrittenSnapshotJoin_ShouldReadOldLayouts()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false, [new("old_w", ["w"], false)]), guard.Token);
        await using (var writer = await database.CreateSessionAsync(guard.Token))
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("INSERT INTO rewrite VALUES(1)", cancellationToken: guard.Token);
        }
        await using var old = await database.CreateSessionAsync(guard.Token);
        await using var snapshot = await old.BeginTransactionAsync(IsolationLevel.Snapshot, guard.Token);

        // Act
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("old_w", ["w"], false), new("ix_v", ["v"], false)]), guard.Token);

        // Assert
        (await RowsAsync(old, "SELECT i.v,r.v FROM items i CROSS JOIN rewrite r WHERE i.w=8", guard.Token))
            .Single().ShouldBe(new object?[] { 7, 1 });
        await using var current = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(current, "SELECT i.v,r.v FROM items i CROSS JOIN rewrite r WHERE i.w=8", guard.Token))
            .Single().ShouldBe(new object?[] { 7, 1L });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: retained indexes preserve registered spelling across case-equivalent declarations")]
    public async Task ApplyAsync_KeptIndexCaseSpelling_ShouldReuseEnforcingTree()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false, [new("IX_V", ["v"], true)]), guard.Token);
        var before = database.IndexManager.ExportRegistrations().Single();
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);

        // Act
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("ix_v", ["v"], true)],
            [new("ck", CompiledSchemaConstraintKind.Check, [], null, [], new("v > 0"))]), guard.Token);

        // Assert
        database.IndexManager.ExportRegistrations().Single().RootPageId.ShouldBe(before.RootPageId);
        (await RowsAsync(writer, "SELECT w FROM items WHERE v=7", guard.Token)).Single()[0].ShouldBe(8);
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(7,9)", cancellationToken: guard.Token));
        await writer.ExecuteAsync("INSERT INTO items VALUES(9,10)", cancellationToken: guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint owner keeping: separate retired trees stay fenced through the publication-marker reader window")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApplyAsync_RetiredTreePublicationWindow_ShouldFenceLateOldReaders(int failure)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        SeedRaw(database, [7, 8], 1);
        await database.ApplySchemaAsync(OwnerDeclaration(true), guard.Token);
        await DrainReclamationAsync(database, guard.Token);
        var previous = Table(database);
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong retiredOwner = database.IndexManager.PageOwner(registration);
        database.IndexManager.TryGetIndex(previous.ObjectId, registration.Definition.Name, out var oldTree).ShouldBeTrue();
        var target = Compiled(OwnerDeclaration(true).Tables.Select(table => table.Name != "items" ? table : new CompiledSchemaTable(
            table.Name, table.RowType, table.Columns.Select(column => column.Name == "v" ? column with { Type = DatabaseType.Int64 } : column).ToArray(),
            table.PrimaryKey, table.Indexes, table.Constraints)).ToArray());
        TransactionContext? lateReader = null;
        int observations = 0;
        bool exposedBeforeMarkerEnd = false, boundOldHeap = false;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "CatalogPublished") { return; }
            observations++;
            exposedBeforeMarkerEnd = database.Catalog.GetPendingTableReclamations().Contains(retiredOwner);
            // Force discovery BEFORE admitting a reader whose snapshot cannot see
            // the still-active publication marker. An early tree fence misses it.
            database.TableReclamation.Request();
            database.TableReclamation.RunBatch(guard.Token);
            lateReader = database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token).AsTask().GetAwaiter().GetResult();
            var image = database.Catalog.CaptureTruncateReadSnapshot(lateReader.Snapshot);
            boundOldHeap = image is not null && image.TryGetTable("dbo", "items", out var table) && ReferenceEquals(table, previous);
            if (failure == 1) { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); }
            if (failure == 2) { throw new OutOfMemoryException("injected post-publication failure"); }
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(target, stop.Token);
            for (int pass = 0; pass < 3; pass++) { database.TableReclamation.RunBatch(guard.Token); }

            // Assert
            observations.ShouldBe(1);
            exposedBeforeMarkerEnd.ShouldBeFalse();
            boundOldHeap.ShouldBeTrue();
            lateReader.ShouldNotBeNull();
            IndexOwnerPages(database, retiredOwner).ShouldBeGreaterThan(0);
            await using var cursor = oldTree.OpenCursor(lateReader.Snapshot, IndexKeyRange.All, cancellationToken: guard.Token);
            (await cursor.MoveNextAsync(guard.Token)).ShouldBeTrue();
        }
        finally
        {
            database.SchemaProvisioner.JointStage = null;
            if (lateReader is not null) { await database.Coordinator.RollbackAsync(lateReader, CancellationToken.None); }
        }
        await DrainReclamationAsync(database, guard.Token);
        IndexOwnerPages(database, retiredOwner).ShouldBe(0);
    }
}
