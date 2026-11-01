using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint space: CHAR padding grows the estimate while VARCHAR bounds preserve stored widths")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_RowWidthGrowth_ShouldChargeOnlyPhysicalExpansion(bool character)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        string kind = character ? "CHAR" : "VARCHAR";
        var source = SqlSchema.Create("typed", schema => schema.Table<WidthOldRow>("items", table =>
            { table.Column(row => row.Value); table.Column(row => row.Code, $"{kind}(10)"); })).Compile();
        SqlCompiledSchema TargetWidth(int width) => SqlSchema.Create("typed", schema => schema.Table<WidthNewRow>("items", table =>
            { table.Column(row => row.Value); table.Column(row => row.Code, $"{kind}({width})"); })).Compile();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("INSERT INTO items VALUES(1,'abc')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        long same = await MeasureNeedAsync(storage.CaptureDurableImages(), TargetWidth(10), guard.Token);
        long wider = await MeasureNeedAsync(storage.CaptureDurableImages(), TargetWidth(100), guard.Token);

        // Assert: ignore declaration-sized floor differences; both intents fit one chunk.
        if (character) { wider.ShouldBeGreaterThan(same); }
        else { wider.ShouldBe(same); }
        SqlCompiledSchema WithNull(int width) => SqlSchema.Create("typed", schema => schema.Table<WidthNullRow>("items", table =>
        {
            table.Column(row => row.Value);
            table.Column(row => row.Code, $"{kind}(10)");
            table.Column(row => row.Added, $"VARCHAR({width})");
        })).Compile();
        long smallNull = await MeasureNeedAsync(storage.CaptureDurableImages(), WithNull(10), guard.Token);
        long wideNull = await MeasureNeedAsync(storage.CaptureDurableImages(), WithNull(8000), guard.Token);
        wideNull.ShouldBe(smallNull);
        _output.WriteLine($"{kind} width allowance: unchanged={same}; width 100={wider} bytes.");
    }

    private sealed record WidthOldRow(int Value, string Code);
    private sealed record WidthNewRow(long Value, string Code);
    private sealed record WidthNullRow(long Value, string Code, string? Added);

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint copy: chunk checkpoints truncate page-image WAL before the next chunk")]
    public async Task ApplyAsync_WideCopy_ShouldCheckpointBetweenChunks()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var storage = new CrashCaptureSqlStorageStrategy(StorageCommitDurability.None);
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = SqlSchema.Create("typed", schema => schema.Table<WideOldRow>("items", table => { table.Column(row => row.Value); table.Column(row => row.Payload, "VARCHAR(4000)"); })).Compile();
        var target = SqlSchema.Create("typed", schema => schema.Table<WideNewRow>("items", table => { table.Column(row => row.Value); table.Column(row => row.Payload, "VARCHAR(4000)"); })).Compile();
        await database.ApplySchemaAsync(source, guard.Token);
        var table = Table(database);
        byte[] record = SqlRowCodec.Encode(table, [1, new string('x', 4000)], TransactionSequence.None);
        for (int start = 0; start < 8192; start += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int i = 0; i < 64; i++) { database.DataStorage.InsertRow(bracket, table.ObjectId, record); }
            bracket.Commit();
        }
        database.Coordinator.Checkpoint(guard.Token);
        int chunks = 0, truncations = 0;
        long last = 0, peak = 0;
        CrashCaptureSqlStorageStrategy? checkpointImage = null;
        storage.DurableStorageChanged = catalog =>
        { if (!catalog) { peak = Math.Max(peak, database.DataStorage.Journal.Length); } };
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "CopyCommitted") { peak = Math.Max(peak, database.DataStorage.Journal.Length); return; }
            if (stage != "Copied") { return; }
            long length = database.DataStorage.Journal.Length;
            chunks++;
            if (length < last)
            {
                truncations++;
                checkpointImage ??= storage.CaptureDurableImages();
            }
            last = length;
            peak = Math.Max(peak, length);
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(target, guard.Token);

            // Assert
            peak.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThanOrEqualTo(SqlSchemaProvisioner.JointCopyCheckpointBytes + 128L * 2 * (Page.Size + 46) + 152);
            chunks.ShouldBe(128);
            truncations.ShouldBeGreaterThan(0);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            _output.WriteLine($"Wide copy: chunks={chunks}; observed truncations={truncations}; between-chunk WAL peak={peak} bytes.");
            await using var reopen = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
            (await reopen.OpenDatabaseAsync("typed", guard.Token)).Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            await using var interrupted = await Builder(checkpointImage.ShouldNotBeNull()).BuildAsync(guard.Token);
            var old = await interrupted.OpenDatabaseAsync("typed", guard.Token);
            old.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
            old.Catalog.PendingMigration.ShouldBeNull();
            await using var oldReader = await old.CreateSessionAsync(guard.Token);
            (await RowsAsync(oldReader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(8192L);
        }
        finally { SqlPlanExecutor.RewriteStage = null; storage.DurableStorageChanged = null; }
    }

    private sealed record WideOldRow(int Value, string Payload);
    private sealed record WideNewRow(long Value, string Payload);

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint space: ten thousand untouched pages cannot change the need for one rewritten page")]
    public async Task ApplyAsync_UntouchedAllocation_ShouldRequireIdenticalBytes()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var small = await SeedAsync(guard.Token, 1);
        long smallNeed = await MeasureNeedAsync(small.CaptureDurableImages(), Target().Compile(), guard.Token);
        await using (var engine = await Builder(small).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE untouched(payload VARCHAR(8000))", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "untouched", out var table).ShouldBeTrue();
            byte[] record = SqlRowCodec.Encode(table, [new string('x', 8000)], TransactionSequence.None);
            for (int first = 0; first < 10000; first += 64)
            {
                using (var bracket = database.DataStorage.BeginTransaction())
                {
                    for (int i = first; i < Math.Min(first + 64, 10000); i++)
                    { database.DataStorage.InsertRow(bracket, table.ObjectId, record); }
                    bracket.Commit();
                }
                database.Coordinator.Checkpoint(guard.Token);
            }
            database.DataStorage.GetOwnerPages(table.ObjectId).Count.ShouldBe(10000);
            small = small.CaptureDurableImages();
        }

        // Act
        long largeNeed = await MeasureNeedAsync(small, Target().Compile(), guard.Token);

        // Assert
        _output.WriteLine($"One-page migration: alone={smallNeed} bytes; with 10000 untouched pages={largeNeed} bytes.");
        largeNeed.ShouldBe(smallNeed);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint space: worker admits the exact need and rejects one byte less, also on resume")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_PreciseSpaceBoundary_ShouldCheckOnWorker(bool below, bool resume)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token, 1);
        var target = Target().Compile();
        long need = await MeasureNeedAsync(storage.CaptureDurableImages(), target, guard.Token);
        if (resume)
        {
            await using var seed = await Builder(storage).BuildAsync(guard.Token);
            var db = await seed.OpenDatabaseAsync("typed", guard.Token);
            await db.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                [new("Joint/AlterColumn", "items", "Value", 1)], 1), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        storage.MigrationAvailableBytes = need - (below ? 1 : 0);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int counts = 0, decoded = 0, owners = 0;
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = db =>
            {
                db.SchemaProvisioner.JointSpaceCounting = () => Interlocked.Increment(ref counts);
                db.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
                db.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { Interlocked.Increment(ref owners); } };
            };
            return new RecordingWorker(owner);
        });
        SqlPlanExecutor.RowDecoded = _ => Interlocked.Increment(ref decoded);
        try
        {
            // Act: Build must succeed even below the precise need; only the floor belongs here.
            await using var engine = await builder.AddDatabase(Target()).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await entered.Task.WaitAsync(guard.Token);

            // Assert
            int buildCounts = counts, buildDecoded = decoded;
            release.Set();
            await WaitDoneAsync(database, guard.Token);
            buildCounts.ShouldBe(0);
            buildDecoded.ShouldBe(0);
            counts.ShouldBe(1);
            database.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.IsOffline.ShouldBeFalse();
            if (below)
            {
                database.Provisioning!.FailureCode.ShouldBe("CS03402");
                database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
                owners.ShouldBe(0);
                decoded.ShouldBe(0);
            }
            else
            {
                database.Provisioning.ShouldBeNull();
                database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
                owners.ShouldBe(1);
                decoded.ShouldBeGreaterThan(0);
                await using var reopen = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
                (await reopen.OpenDatabaseAsync("typed", guard.Token)).Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            }
        }
        finally { release.Set(); SqlPlanExecutor.RowDecoded = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint space: worker counts actual multi-level index pages")]
    public async Task ApplyAsync_IndexAllocation_ShouldCountRealTrees()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token, 4096);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        database.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { throw new InvalidOperationException("stop after admission"); } };
        long heap = database.DataStorage.GetOwnerPages(Table(database).ObjectId).Count;
        // This fixture has exactly one heap owner and its two trees in the data file.
        long actualTrees = database.DataStorage.PageManager.PageCount - database.DataStorage.PageManager.FreePageCount - heap - 2;

        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));

        // Assert
        database.SchemaProvisioner.JointSpaceIndexPages.ShouldBe(actualTrees);
        actualTrees.ShouldBeGreaterThan(2);
        _output.WriteLine($"Rewritten heap={heap} pages; actual index trees={actualTrees} pages; need={database.SchemaProvisioner.JointSpaceNeed} bytes.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint space: a large copy that cannot fit is refused online before prepared writes")]
    public async Task ApplyAsync_LargeCopy_ShouldRefuseAndDiscardIntentOnWorker()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = await SeedAsync(guard.Token, 0);
        long copyBytes;
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var db = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var session = await db.CreateSessionAsync(guard.Token);
            for (int first = 1; first <= 32768; first += 4096)
            {
                await session.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',',
                    Enumerable.Range(first, 4096).Select(id => $"({id},{id})")), cancellationToken: guard.Token);
            }
            copyBytes = (db.DataStorage.PageManager.PageCount - db.DataStorage.PageManager.FreePageCount - 2) * Page.Size;
            storage = storage.CaptureDurableImages();
        }
        long need = await MeasureNeedAsync(storage.CaptureDurableImages(), Target().Compile(), guard.Token);
        storage.MigrationAvailableBytes = 2 * 1024 * 1024;
        copyBytes.ShouldBeGreaterThan(storage.MigrationAvailableBytes);
        need.ShouldBeGreaterThan(copyBytes);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        int owners = 0;
        database.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { owners++; } };

        // Act
        await database.SchemaProvisioner.ApplyAsync(Target().Compile(), guard.Token, true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        owners.ShouldBe(0);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.IsOffline.ShouldBeFalse();
        _output.WriteLine($"Large copy: source heap/trees={copyBytes} bytes; need={need}; available={storage.MigrationAvailableBytes}; code={database.Provisioning.FailureCode}.");
    }

    private static async Task<long> MeasureNeedAsync(CrashCaptureSqlStorageStrategy storage, SqlCompiledSchema target, CancellationToken token)
    {
        await using var engine = await Builder(storage).BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("typed", token);
        database.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { throw new InvalidOperationException("stop after admission"); } };
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, token));
        database.SchemaProvisioner.JointSpaceNeed.ShouldBeGreaterThan(0);
        return database.SchemaProvisioner.JointSpaceNeed;
    }
}
