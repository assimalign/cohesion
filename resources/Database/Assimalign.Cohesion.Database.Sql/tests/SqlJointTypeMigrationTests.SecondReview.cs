using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CheckAsync")]
    private static extern ValueTask<HealthCheckResult> SecondReviewHealthAsync(DatabaseApplicationContext context, CancellationToken token);

    private sealed record RetirementRow(int Id, string LeftValue, string RightValue);

    private static SqlSchema RetirementDeclaration(bool indexes) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<RetirementRow>("items", table =>
        {
            table.Key(row => row.Id);
            table.Column(row => row.LeftValue, "VARCHAR(400)");
            table.Column(row => row.RightValue, "VARCHAR(400)");
            if (indexes) { table.Index(row => row.LeftValue); table.Index(row => row.RightValue); }
        });
    });

    private static async Task<CrashCaptureSqlStorageStrategy> SeedStrandedRetirementAsync(CancellationToken token)
    {
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).AddDatabase(RetirementDeclaration(true)).BuildAsync(token);
        var db = await engine.OpenDatabaseAsync("typed", token);
        ulong owner = Table(db).ObjectId;
        db.IndexManager.ExportRegistrations().All(index => db.IndexManager.PageOwner(index) == owner).ShouldBeTrue();
        await using var writer = await db.CreateSessionAsync(token);
        for (int start = 1; start <= 8192; start += 64)
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(start, 64).Select(id =>
                $"({id},'{id:D4}{new string('a', 396)}','{id:D4}{new string('b', 396)}')")), cancellationToken: token);
        }
        await db.SchemaProvisioner.ApplyAsync(RetirementDeclaration(false).Compile(), token);
        Table(db).ObjectId.ShouldBe(owner);
        db.IndexManager.ExportRegistrations().Count.ShouldBe(1);
        IndexOwnerPages(db, owner).ShouldBeGreaterThan(1000);
        db.Coordinator.Checkpoint(token); db.CatalogStorage.Checkpoint();
        return storage.CaptureDurableImages();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Second review: stranded retirement admits at a real cap and refuses one page under (#1778)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyAsync_SecondReviewStrandedRetirement_ShouldHoldCapThroughReclamation(bool sparse, bool typeRewrite)
    {
        // Arrange: two 400-character secondary trees were unregistered from an 8192-row heap.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var source = RetirementDeclaration(false).Compile();
        var target = typeRewrite ? ChangeItems(source, columns: source.Tables.Single().Columns.Select(column =>
            column.Name == "Id" ? column with { Type = DatabaseType.Int64 } : column).ToArray()) : Compiled();
        var seed = await SeedStrandedRetirementAsync(guard.Token);
        long need = 0;
        foreach (int run in new[] { 0, 1, 2 })
        {
            var image = seed.CaptureDurableImages(); image.UnixSparseGrowth = sparse;
            var builder = Builder(image);
            builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
            await using var engine = await builder.BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            ulong owner = Table(db).ObjectId;
            long heapPages = db.DataStorage.GetOwnerPages(owner).Count;
            long indexPages = IndexOwnerPages(db, owner);
            long before = image.AllocatedBytes;
            int intents = 0;
            db.SchemaProvisioner.JointStage = stage => { if (stage == "IntentDurable") { intents++; } };
            if (run != 0) { image.VolumeCapacityBytes = before + need - (run == 2 ? Page.Size : 0); }
            try
            {
                // Act / Assert
                if (run == 2)
                {
                    var refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.ApplySchemaAsync(target, guard.Token));
                    refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                    intents.ShouldBe(0); db.Catalog.PendingMigration.ShouldBeNull();
                    db.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
                    db.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
                    // Only logical reservation Begin/Rollback frames may remain.
                    (image.AllocatedBytes - before).ShouldBeLessThan(Page.Size);
                }
                else
                {
                    await db.ApplySchemaAsync(target, guard.Token);
                    if (run == 0) { need = db.SchemaProvisioner.JointSpaceNeed; }
                    db.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                    await DrainReclamationAsync(db, guard.Token);
                    IndexOwnerPages(db, owner).ShouldBe(0); db.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
                    db.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
                    db.Provisioning.ShouldBeNull(); db.OfflineError.ShouldBeNull();
                    await using (var application = DatabaseApplication.CreateBuilder([]).AddEngine(engine).Build())
                    { (await SecondReviewHealthAsync(application.Context, guard.Token)).Status.ShouldBe(HealthStatus.Healthy); }
                    long free = db.DataStorage.PageManager.FreePageCount;
                    await DrainReclamationAsync(db, guard.Token);
                    db.DataStorage.PageManager.FreePageCount.ShouldBe(free);
                    _output.WriteLine($"retirement sparse={sparse} type={typeRewrite} run={run}: N={need}; heap={heapPages}; indexes={indexPages}; growth={image.AllocatedBytes - before}; peak={image.PeakAllocatedBytes}; cap={image.VolumeCapacityBytes}");
                }
                db.IsOffline.ShouldBeFalse(); image.CapacityFailures.ShouldBe(0);
            }
            finally { image.VolumeCapacityBytes = long.MaxValue; }
        }
    }

    private static SqlCompiledSchema FrozenDeclaration(string kind, int generation, int precision = 8, bool invalidAppend = false)
    {
        var columns = new List<CompiledSchemaColumn> { new("id", DatabaseType.Int32, false) };
        if (generation != 0)
        {
            bool binary = kind == "binary";
            columns.Add(new("v", binary ? DatabaseType.Binary : DatabaseType.Decimal, true,
                MaxLength: binary ? precision : null, Precision: binary ? null : precision, Scale: binary ? null : 0,
                CustomType: kind == "domain" ? "amount" : null,
                DefaultLiteral: generation == 1 || invalidAppend ? binary ? "X'0102030405'" : "12345" : binary ? "X'01'" : "1"));
        }
        if (generation >= 3) { columns.Add(new("second", DatabaseType.Decimal, true, Precision: precision, Scale: 0, DefaultLiteral: generation == 3 ? "23456" : "2")); }
        return Compiled(new CompiledSchemaTable("items", "row", columns, new("pk", ["id"]), [], []));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Second review: TRUNCATE drains stranded pages within the measured heap-retirement budget (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SecondReviewStrandedTruncate_ShouldFitRetirementBudget(bool sparse)
    {
        // Arrange: measure the joint DROP retirement budget on an identical image.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var seed = await SeedStrandedRetirementAsync(guard.Token);
        long need;
        await using (var measure = await Builder(seed.CaptureDurableImages()).BuildAsync(guard.Token))
        {
            var db = await measure.OpenDatabaseAsync("typed", guard.Token);
            await db.ApplySchemaAsync(Compiled(), guard.Token);
            need = db.SchemaProvisioner.JointSpaceNeed;
        }
        var image = seed.CaptureDurableImages(); image.UnixSparseGrowth = sparse;
        var builder = Builder(image); builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        ulong owner = Table(current).ObjectId;
        image.VolumeCapacityBytes = image.AllocatedBytes + need;
        try
        {
            await using var writer = await current.CreateSessionAsync(guard.Token);
            // Act
            await writer.ExecuteAsync("TRUNCATE TABLE items", cancellationToken: guard.Token);
            await DrainReclamationAsync(current, guard.Token);
            // Assert
            IndexOwnerPages(current, owner).ShouldBe(0); current.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
            current.Catalog.GetPendingTableReclamations().ShouldBeEmpty(); current.IsOffline.ShouldBeFalse();
            image.CapacityFailures.ShouldBe(0);
            await using var application = DatabaseApplication.CreateBuilder([]).AddEngine(engine).Build();
            (await SecondReviewHealthAsync(application.Context, guard.Token)).Status.ShouldBe(HealthStatus.Healthy);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,'new','new')", cancellationToken: guard.Token);
            _output.WriteLine($"TRUNCATE sparse={sparse}: retirement budget={need}; cap={image.VolumeCapacityBytes}; peak={image.PeakAllocatedBytes}");
        }
        finally { image.VolumeCapacityBytes = long.MaxValue; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Second review: only visible frozen values constrain tightening, including reopen and later writes (#1778)")]
    [InlineData("decimal", false)]
    [InlineData("decimal", true)]
    [InlineData("binary", false)]
    [InlineData("binary", true)]
    [InlineData("generations", false)]
    [InlineData("generations", true)]
    public async Task ApplyAsync_SecondReviewFrozenTightening_ShouldValidateVisibleRows(string kind, bool visibleMissing)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var image = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(image);
        SqlCompiledSchema target;
        ulong owner;
        await using (var engine = await builder.BuildAsync(guard.Token))
        {
            var db = await engine.CreateDatabaseAsync("typed", guard.Token);
            await db.ApplySchemaAsync(FrozenDeclaration(kind, 0), guard.Token);
            owner = Table(db).ObjectId;
            await using var writer = await db.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            await db.ApplySchemaAsync(FrozenDeclaration(kind, 1), guard.Token);
            if (!visibleMissing) { await writer.ExecuteAsync(kind == "binary" ? "UPDATE items SET v=X'01'" : "UPDATE items SET v=1", cancellationToken: guard.Token); }
            await db.ApplySchemaAsync(FrozenDeclaration(kind, 2), guard.Token);
            int generation = 2;
            if (kind == "generations")
            {
                await db.ApplySchemaAsync(FrozenDeclaration(kind, 3), guard.Token);
                if (!visibleMissing) { await writer.ExecuteAsync("UPDATE items SET second=2", cancellationToken: guard.Token); }
                await db.ApplySchemaAsync(FrozenDeclaration(kind, 4), guard.Token);
                generation = 4;
            }
            target = FrozenDeclaration(kind, generation, 3);
            string oldHash = db.Catalog.SchemaState!.ContentHash;
            // Act / Assert
            if (visibleMissing)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.ApplySchemaAsync(target, guard.Token));
                failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                db.Catalog.SchemaState!.ContentHash.ShouldBe(oldHash); Table(db).ObjectId.ShouldBe(owner);
                db.IsOffline.ShouldBeFalse(); db.Catalog.PendingMigration.ShouldBeNull();
                return;
            }
            await db.ApplySchemaAsync(target, guard.Token);
            Table(db).ObjectId.ShouldBe(owner); db.Provisioning.ShouldBeNull();
            await writer.ExecuteAsync(kind == "generations" ? "INSERT INTO items(id,v,second) VALUES(2,1,2)" : kind == "binary" ? "INSERT INTO items(id,v) VALUES(2,X'01')" : "INSERT INTO items(id,v) VALUES(2,1)", cancellationToken: guard.Token);
            db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint();
            image = image.CaptureDurableImages();
        }
        var reopen = Builder(image);
        await using var recovered = await reopen.BuildAsync(guard.Token);
        var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        var rows = await RowsAsync(reader, "SELECT v FROM items ORDER BY id", guard.Token);
        rows.Length.ShouldBe(2);
        if (kind == "binary") { ((byte[])rows[0][0]!).ShouldBe(new byte[] { 1 }); ((byte[])rows[1][0]!).ShouldBe(new byte[] { 1 }); }
        else { rows[0][0].ShouldBe(1m); rows[1][0].ShouldBe(1m); }
        current.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Second review: a domain's precision tightening validates visible frozen values (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_SecondReviewFrozenDomain_ShouldValidateVisibleRows(bool visibleMissing)
    {
        // Arrange: the new application's same-name native domain has a tighter base.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var image = new CrashCaptureSqlStorageStrategy();
        var seedBuilder = Builder(image);
        seedBuilder.Types.Add(SqlType.CreateDomain("amount", SqlType.Decimal(8, 0)));
        ulong owner;
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var db = await seed.CreateDatabaseAsync("typed", guard.Token);
            await db.ApplySchemaAsync(FrozenDeclaration("domain", 0), guard.Token);
            owner = Table(db).ObjectId;
            await using var writer = await db.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            await db.ApplySchemaAsync(FrozenDeclaration("domain", 1), guard.Token);
            if (!visibleMissing) { await writer.ExecuteAsync("UPDATE items SET v=1", cancellationToken: guard.Token); }
            await db.ApplySchemaAsync(FrozenDeclaration("domain", 2), guard.Token);
            db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint();
            image = image.CaptureDurableImages();
        }
        var builder = Builder(image);
        builder.Types.Add(SqlType.CreateDomain("amount", SqlType.Decimal(3, 0)));
        await using (var engine = await builder.BuildAsync(guard.Token))
        {
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            var target = FrozenDeclaration("domain", 2, 3);
            string oldHash = db.Catalog.SchemaState!.ContentHash;
            // Act / Assert
            if (visibleMissing)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.ApplySchemaAsync(target, guard.Token));
                failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                db.Catalog.SchemaState!.ContentHash.ShouldBe(oldHash); db.IsOffline.ShouldBeFalse();
                return;
            }
            await db.ApplySchemaAsync(target, guard.Token);
            Table(db).ObjectId.ShouldBe(owner); db.Provisioning.ShouldBeNull();
            await using var writer = await db.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items(id) VALUES(2)", cancellationToken: guard.Token);
            db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint(); image = image.CaptureDurableImages();
        }
        var reopen = Builder(image); reopen.Types.Add(SqlType.CreateDomain("amount", SqlType.Decimal(3, 0)));
        await using var recovered = await reopen.BuildAsync(guard.Token);
        var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
        await using var reader = await current.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT v FROM items ORDER BY id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { 1m, 1m });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Second review: a newly appended missing value must fit its declaration (#1778)")]
    public async Task ApplyAsync_SecondReviewInvalidFrozenAppend_ShouldRefuseBeforeIntent()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var db = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = FrozenDeclaration("decimal", 0);
        await db.ApplySchemaAsync(source, guard.Token);
        await using var writer = await db.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.ApplySchemaAsync(FrozenDeclaration("decimal", 1, 3, invalidAppend: true), guard.Token));
        // Assert
        _output.WriteLine($"invalid append: {string.Join(',', failure.Diagnostics.Select(diagnostic => diagnostic.Code))}; {failure.Message}");
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS02002");
        db.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash); db.Catalog.PendingMigration.ShouldBeNull(); db.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Second review: stranded page retirement WAL follows the actual image/delta encoding (#1778)")]
    public async Task ApplyAsync_SecondReviewStrandedWal_ShouldCoverEveryRetiredPage()
    {
        // Arrange: exclude background reclamation with a statement pin, then use
        // its production FreeOwnerPages path one page per durable bracket.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var image = await SeedStrandedRetirementAsync(guard.Token);
        var builder = Builder(image);
        builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
        await using var engine = await builder.BuildAsync(guard.Token);
        var db = await engine.OpenDatabaseAsync("typed", guard.Token);
        ulong owner = Table(db).ObjectId;
        var registered = new HashSet<long>();
        var pending = new Stack<long>(db.IndexManager.ExportRegistrations().Select(index => index.RootPageId));
        while (pending.TryPop(out long registeredPage))
        {
            registered.Add(registeredPage).ShouldBeTrue();
            using var handle = db.DataStorage.PageManager.GetPage((PageId)registeredPage);
            var body = handle.Page.AsBodySpan();
            if (body[3] != 2) { continue; }
            pending.Push(BinaryPrimitives.ReadInt64LittleEndian(body[24..]));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            for (int entry = 0; entry < count; entry++)
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(body[(32 + 2 * entry)..]);
                int header = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
                pending.Push(BinaryPrimitives.ReadInt64LittleEndian(body[(offset + 2 + (header & 0x3fff) + (header >> 14) * 8)..]));
            }
        }
        var indexes = new SortedSet<long>(); var stranded = new HashSet<long>();
        for (long id = 1; id < db.DataStorage.PageManager.PageCount; id++)
        {
            using var handle = db.DataStorage.PageManager.GetPage((PageId)id);
            if (handle.Page.OwnerId != owner || handle.Page.Type != PageType.Index) { continue; }
            indexes.Add(id);
            if (!registered.Contains(id)) { stranded.Add(id); }
        }
        stranded.Count.ShouldBeGreaterThan(900);
        db.Coordinator.Checkpoint(guard.Token);
        long pin = db.TableReclamation.EnterStatement();
        long total = 0, allWal = 0, minimum = long.MaxValue, maximum = 0; int sampled = 0;
        try
        {
            await db.ApplySchemaAsync(Compiled(), guard.Token);
            // Act
            while (db.DataStorage.HasOwnerPages(owner) || indexes.Count != 0)
            {
                guard.Token.ThrowIfCancellationRequested();
                var heap = db.DataStorage.GetOwnerPages(owner);
                long next = heap.Count == 0 ? indexes.Min : (long)heap[0];
                long start = db.DataStorage.JournalLength;
                using (var bracket = db.DataStorage.BeginTransaction("SqlTableReclamation", unchecked((long)owner)))
                {
                    db.DataStorage.FreeOwnerPages(bracket, owner, 1).ShouldBe(1);
                    bracket.Commit();
                }
                long bytes = db.DataStorage.JournalLength - start;
                allWal += bytes;
                indexes.Remove(next);
                if (!stranded.Contains(next)) { continue; }
                // Begin/Commit framing is 76 B. The other bytes are this page's
                // optional pre-image and its committed delta/cleared image.
                long pageBytes = bytes - 76;
                total += pageBytes; minimum = Math.Min(minimum, pageBytes); maximum = Math.Max(maximum, pageBytes); sampled++;
            }
            // Assert: the existing reserve's WAL component must cover measured
            // stranded work alone, independently of the free-space margins.
            sampled.ShouldBe(stranded.Count);
            db.SchemaProvisioner.JointSpaceDiscardBytes.ShouldBeGreaterThan(allWal);
            _output.WriteLine($"stranded WAL: pages={sampled}; page bytes min={minimum} max={maximum} total={total}; per-bracket framing=76; all retirement WAL={allWal}; E={db.SchemaProvisioner.JointSpaceDiscardBytes}");
        }
        finally { db.TableReclamation.ExitStatement(pin); }
        await DrainReclamationAsync(db, guard.Token);
        db.Catalog.GetPendingTableReclamations().ShouldBeEmpty(); db.IsOffline.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Second review: seeded frozen-value sequences match forced replacement (#1778)")]
    public async Task ApplyAsync_SecondReviewFrozenDifferential_ShouldMatchForcedRewrite()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        const int seed = 202611052;
        const int pairs = 48;
        var random = new Random(seed);
        int refusals = 0, successes = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            string kind = pair % 3 == 0 ? "binary" : pair % 3 == 1 ? "generations" : "decimal";
            bool visible = pair % 4 == 0;
            int first = random.Next(1, 100), second = first + random.Next(1, 100);
            var results = new List<string>[2];
            for (int mode = 0; mode < 2; mode++)
            {
                var image = new CrashCaptureSqlStorageStrategy();
                results[mode] = new();
                await using (var engine = await Builder(image).BuildAsync(guard.Token))
                {
                    var db = await engine.CreateDatabaseAsync("typed", guard.Token);
                    db.SchemaProvisioner.ForceJointRewrite = mode == 1;
                    await db.ApplySchemaAsync(FrozenDeclaration(kind, 0), guard.Token);
                    await using var writer = await db.CreateSessionAsync(guard.Token);
                    await writer.ExecuteAsync($"INSERT INTO items VALUES({first}),({second})", cancellationToken: guard.Token);
                    // Act: append, overwrite some/all tails, change defaults, add
                    // a second frozen generation, then tighten and reopen.
                    await db.ApplySchemaAsync(FrozenDeclaration(kind, 1), guard.Token);
                    await writer.ExecuteAsync((kind == "binary" ? "UPDATE items SET v=X'01'" : "UPDATE items SET v=1") + (visible ? $" WHERE id={first}" : string.Empty), cancellationToken: guard.Token);
                    await db.ApplySchemaAsync(FrozenDeclaration(kind, 2), guard.Token);
                    if (kind == "generations")
                    {
                        await db.ApplySchemaAsync(FrozenDeclaration(kind, 3), guard.Token);
                        await writer.ExecuteAsync("UPDATE items SET second=2" + (visible ? $" WHERE id={first}" : string.Empty), cancellationToken: guard.Token);
                        await db.ApplySchemaAsync(FrozenDeclaration(kind, 4), guard.Token);
                    }
                    var failure = await Record.ExceptionAsync(async () => await db.ApplySchemaAsync(FrozenDeclaration(kind, kind == "generations" ? 4 : 2, 3), guard.Token));
                    results[mode].Add(failure is DatabaseException coded ? string.Join(',', coded.Diagnostics.Select(diagnostic => diagnostic.Code)) : failure?.GetType().Name ?? "success");
                    db.IsOffline.ShouldBeFalse(); db.Catalog.PendingMigration.ShouldBeNull();
                    results[mode].AddRange(await CaptureDifferentialSnapshotAsync(db, guard.Token, "id"));
                    await writer.ExecuteAsync(kind == "generations" ? "INSERT INTO items(id,v,second) VALUES(1000,1,2)" : kind == "binary" ? "INSERT INTO items(id,v) VALUES(1000,X'01')" : "INSERT INTO items(id,v) VALUES(1000,1)", cancellationToken: guard.Token);
                    db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint(); image = image.CaptureDurableImages();
                }
                await using var recovered = await Builder(image).BuildAsync(guard.Token);
                results[mode].AddRange(await CaptureDifferentialSnapshotAsync(await recovered.OpenDatabaseAsync("typed", guard.Token), guard.Token, "id"));
            }
            // Assert
            results[0].ShouldBe(results[1], $"seed={seed}, pair={pair}, kind={kind}, visible={visible}");
            results[0][0].ShouldBe(visible ? "CS03402" : "success");
            if (visible) { refusals++; } else { successes++; }
        }
        _output.WriteLine($"frozen differential seed={seed}; pairs={pairs}; success={successes}; matching refusals={refusals}; differences=0");
    }
}
