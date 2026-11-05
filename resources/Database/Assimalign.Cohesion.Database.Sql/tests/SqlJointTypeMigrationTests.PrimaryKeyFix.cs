using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record KeyCostRow(int Id, int Part, int Value);
    private sealed record KeyCostWideRow(int Id, int Part, long Value);

    private static SqlSchema KeyDeclaration(bool target, bool rewrite = false, bool created = false)
        => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            if (target && rewrite)
            {
                schema.Table<KeyCostWideRow>("items", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Part);
                    table.Column(row => row.Value);
                    table.Index(row => row.Value);
                });
            }
            else
            {
                schema.Table<KeyCostRow>("items", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Part);
                    table.Column(row => row.Value);
                    if (target) { table.Index(row => row.Value); }
                });
            }
            if (created) { schema.Table<DeferredCreated>("created", table => table.Key(row => row.Id)); }
        });

    // The public table.Key DSL accepts one direct member. Exercise composite keys
    // through its compiled-schema contract, using the same empty per-step executor.
    private static SqlCompiledSchema KeyTarget(bool target, bool composite = false, bool rewrite = false, bool created = false)
    {
        var compiled = KeyDeclaration(target, rewrite, created).Compile();
        return composite ? Compiled(compiled.Tables.Select(table => table.Name == "items"
            ? new CompiledSchemaTable(table.Name, table.RowType, table.Columns, new("pk", ["Id", "Part"]), table.Indexes, table.Constraints)
            : table.Name == "created" ? new CompiledSchemaTable(table.Name, table.RowType,
                [new("Id", DatabaseType.Int32, false), new("Part", DatabaseType.Int32, false)],
                new("pk_created", ["Id", "Part"]), [], [new("fk_created_items", CompiledSchemaConstraintKind.Reference, ["Id", "Part"], "items", ["Id", "Part"])])
            : table).ToArray()) : compiled;
    }

    private static async Task SeedKeyCostRowsAsync(SqlDatabase database, int count, CancellationToken token)
    {
        await using var writer = await database.CreateSessionAsync(token);
        for (int first = 0; first < count; first += 64)
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(first, Math.Min(64, count - first))
                .Select(id => $"({id},{id % 7},{id % 113})")), cancellationToken: token);
            if ((first + 64) % 4096 == 0) { database.Coordinator.Checkpoint(token); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint PK fix: first additive index keeps column and composite primary-key heaps within ten percent of 49a (#1820)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApplyAsync_PrimaryKeyFixAdditiveCost_ShouldKeepHeapAndBoundWrites(bool composite, bool created)
    {
        // Arrange: the initial empty Build takes the ordinary per-step CREATE route.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var source = KeyTarget(false, composite);
        var target = KeyTarget(true, composite, created: created);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase("typed", db => { if (!composite) { db.Schema(KeyDeclaration(false)); } }).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            if (composite) { await database.ApplySchemaAsync(source, guard.Token); }
            await SeedKeyCostRowsAsync(database, 32768, guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        var measured = await MeasureDeferredCostAsync(storage, source, target, guard.Token);

        // Assert
        _output.WriteLine($"PK additive: composite={composite}; new table={created}; 49a={measured.OldPages}; joint={measured.JointPages}; rewritten={measured.Rewritten}; kept heap={measured.KeepsHeap}; ratio={(double)measured.JointPages / measured.OldPages:P2}");
        measured.Rewritten.ShouldBe(0);
        measured.KeepsHeap.ShouldBeTrue();
        measured.KeepsOwner.ShouldBeTrue();
        measured.Threshold.ShouldBe(64L * 1024 * 1024);
        measured.JointPages.ShouldBeLessThanOrEqualTo(measured.OldPages * 110 / 100);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint PK fix: keep and rewrite preserve the key name in metadata, violations and ON CONFLICT (#1820)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyAsync_PrimaryKeyFixVisibleName_ShouldStayStable(bool composite, bool rewrite)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new()).AddDatabase("typed", db => { if (!composite) { db.Schema(KeyDeclaration(false)); } }).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        if (composite) { await database.ApplySchemaAsync(KeyTarget(false, true), guard.Token); }
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,3,7)", cancellationToken: guard.Token);
        string name = database.Catalog.GetIndexes(Table(database).ObjectId).Single(index => index.IsPrimaryKey).Name;
        ulong heap = Table(database).ObjectId;
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase == "IntentDurable")
            { database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == (rewrite ? "Joint/RewriteOwner" : "Joint/KeepOwner")); }
        };

        // Act
        await database.SchemaProvisioner.ApplyAsync(KeyTarget(true, composite, rewrite), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        (Table(database).ObjectId == heap).ShouldBe(!rewrite);
        database.Catalog.GetIndexes(Table(database).ObjectId).Single(index => index.IsPrimaryKey).Name.ShouldBe(name);
        (await RowsAsync(writer, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME='items' AND CONSTRAINT_TYPE='PRIMARY KEY'", guard.Token)).Single()[0].ShouldBe(name);
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,3,8)", cancellationToken: guard.Token));
        failure.Message.ShouldContain(name, Case.Sensitive);
        await writer.ExecuteAsync($"INSERT INTO items VALUES(1,3,9) ON CONFLICT ON CONSTRAINT {name} DO UPDATE SET Value=EXCLUDED.Value", cancellationToken: guard.Token);
        Convert.ToInt64((await RowsAsync(writer, "SELECT Value FROM items", guard.Token)).Single()[0]).ShouldBe(9);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint PK fix: a new joint table exposes the same primary-key name as per-step creation (#1820)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_PrimaryKeyFixNewTable_ShouldUsePerStepName(bool composite)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var ordinary = await Builder(new()).BuildAsync(guard.Token);
        var perStep = await ordinary.CreateDatabaseAsync("typed", guard.Token);
        await perStep.ApplySchemaAsync(KeyTarget(true, composite, created: true), guard.Token);
        string Name(SqlDatabase database) => database.Catalog.GetIndexes(database.Catalog.Tables.Single(table => table.Name == "created").ObjectId).Single(index => index.IsPrimaryKey).Name;
        string expected = Name(perStep);
        await using var engine = await Builder(new()).AddDatabase("typed", db => { if (!composite) { db.Schema(KeyDeclaration(false)); } }).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        if (composite) { await database.ApplySchemaAsync(KeyTarget(false, true), guard.Token); }
        await SeedKeyCostRowsAsync(database, 1, guard.Token);

        // Act
        await database.SchemaProvisioner.ApplyAsync(KeyTarget(true, composite, created: true), guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        Name(database).ShouldBe(expected);
        await using var ordinaryReader = await perStep.CreateSessionAsync(guard.Token);
        await using var jointReader = await database.CreateSessionAsync(guard.Token);
        const string query = "SELECT CONSTRAINT_NAME,CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME='created' ORDER BY CONSTRAINT_NAME";
        var ordinaryNames = await RowsAsync(ordinaryReader, query, guard.Token);
        var jointNames = await RowsAsync(jointReader, query, guard.Token);
        jointNames.Select(row => string.Join('|', row)).ShouldBe(ordinaryNames.Select(row => string.Join('|', row)));
        if (composite)
        {
            jointNames.Length.ShouldBe(2);
            database.Catalog.Tables.Single(table => table.Name == "created").PrimaryKeyColumns.ShouldBe(new[] { "Id", "Part" });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint PK fix: an old RewriteOwner intent is discarded and replanned to KeepOwner (#1820)")]
    public async Task ApplyAsync_PrimaryKeyFixOldIntent_ShouldDiscardAndReplan()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new()).AddDatabase(KeyDeclaration(false)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await SeedKeyCostRowsAsync(database, 1, guard.Token);
        ulong heap = Table(database).ObjectId;
        var target = KeyDeclaration(true).Compile();
        await database.Catalog.SavePendingMigrationAsync(new SqlCatalogPendingMigration(target.Hash, target.CanonicalDocument,
            [new("Joint/AddIndex", "items", target.Tables.Single().Indexes.Single().Name, 1), new("Joint/RewriteOwner", "items", "items", 2)], 2), guard.Token);
        using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, System.Diagnostics.Tracing.EventLevel.Verbose);

        // Act
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        events.Events.ShouldContain(entry => entry.EventId == 14 && Equals(entry.Payload![5], "Restarted"));
        events.Events.ShouldContain(entry => entry.EventId == 13 && Equals(entry.Payload![4], "Replan"));
        Table(database).ObjectId.ShouldBe(heap);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Provisioning.ShouldBeNull();
    }
}
