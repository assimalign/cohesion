using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint script index fix: TYPE and deferred index plans preserve a script's unique tree and enforcement (#1821)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildAsync_ScriptIndexFixPublication_ShouldKeepUniqueIndex(bool rewrite)
    {
        // Arrange: TYPE retains the declaration; the deferred plan removes it from code.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const string script = "CREATE UNIQUE INDEX script_unique ON items(Part)";
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong heap;
        var seedBuilder = Builder(storage).AddDatabase("typed", db => { db.Schema(KeyDeclaration(false)); db.Script("001", script); });
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            heap = Table(database).ObjectId;
            var tree = database.IndexManager.ExportRegistrations().Single(index => index.Definition.Name == "script_unique");
            database.IndexManager.PageOwner(tree).ShouldBe(heap);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,3,7)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        int handoffs = 0;
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase != "IntentDurable") { return; }
                database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == (rewrite ? "Joint/RewriteOwner" : "Joint/KeepOwner"));
                handoffs++;
            };
            return new RecordingWorker(engine);
        });
        builder.AddDatabase("typed", db =>
        {
            db.Schema(KeyDeclaration(true, rewrite: rewrite));
            if (rewrite) { db.Script("001", script); }
        });

        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(current, guard.Token);

        // Assert: retaining this heap-owned script index cannot force a rewrite.
        handoffs.ShouldBe(1);
        (Table(current).ObjectId == heap).ShouldBe(!rewrite);
        current.Catalog.GetIndexes(Table(current).ObjectId).ShouldContain(index => index.Name == "script_unique" && index.IsUnique);
        current.Catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldContain("I:\"dbo\".\"items\".\"script_unique\"");
        current.Provisioning.ShouldBeNull();
        await using var session = await current.CreateSessionAsync(guard.Token);
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT INTO items VALUES(2,3,8)", cancellationToken: guard.Token));
        failure.Message.ShouldContain("script_unique", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint script index fix: dropping a script-index dependency refuses before intent save (#1821)")]
    public async Task ApplyAsync_ScriptIndexFixDroppedColumn_ShouldRefuseAndPreserveOldSchema()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new()).AddDatabase("typed", db =>
        { db.Schema(KeyDeclaration(false)); db.Script("001", "CREATE UNIQUE INDEX script_unique ON items(Part)"); }).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await SeedKeyCostRowsAsync(database, 1, guard.Token);
        var source = KeyDeclaration(false).Compile();
        var original = source.Tables.Single();
        var target = Compiled(new CompiledSchemaTable(original.Name, original.RowType,
            original.Columns.Where(column => column.Name != "Part").ToArray(), original.PrimaryKey,
            [new("ix_value", ["Value"], false)], []));

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true));

        // Assert
        failure.Message.ShouldContain("Part", Case.Sensitive);
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.Catalog.GetIndexes(Table(database).ObjectId).ShouldContain(index => index.Name == "script_unique");
        database.IsOffline.ShouldBeFalse();
    }
}
