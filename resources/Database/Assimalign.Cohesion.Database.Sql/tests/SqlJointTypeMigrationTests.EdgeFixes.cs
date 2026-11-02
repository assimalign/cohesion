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
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Non-TYPE defaults: deferred Build admission refuses before saving anything")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_NonTypeDefaultChange_ShouldRefuseBeforeIntent(bool notNull)
    {
        // Arrange: precision narrowing stays on 49a; unlike VARCHAR narrowing it never trims.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token, 0)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var source = Compiled(new CompiledSchemaTable("items", "row", [new("code", DatabaseType.Decimal, true, Precision: 10, Scale: 0, DefaultLiteral: "1")], null, [], []));
        // Use a separate database because Seed's objects are deliberately schema owned.
        database = await engine.CreateDatabaseAsync("defaults", guard.Token);
        SqlCompiledSchema Named(SqlCompiledSchema value) => new(value.Format, "defaults", true, [], value.Tables, []);
        source = Named(source);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        var target = Named(Compiled(new CompiledSchemaTable("items", "row", [new("code", DatabaseType.Decimal, !notNull, Precision: 5, Scale: 0, DefaultLiteral: "2")], null, [], [])));
        int writes = 0;
        database.Catalog.BeforeJointDiscard = () => throw new InvalidOperationException("No joint cleanup should run");
        var before = database.Catalog.DdlGeneration;
        // Act: this is the same deferDataScalingSteps entry used by typed Build.
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.DdlGeneration.ShouldBe(before);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.OfflineError.ShouldBeNull();
        database.SchemaProvisioner.StepApplied = _ => writes++;
        (await database.SchemaProvisioner.ApplyAsync(source, guard.Token, true)).WasAlreadyApplied.ShouldBeTrue();
        writes.ShouldBe(0);
        (await RowsAsync(session, "SELECT code FROM items", guard.Token)).Single()[0].ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - VARCHAR narrowing: assignment trimming uses joint publication and rollback Build stays available")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_VarcharNarrowing_ShouldUseJointAndKeepRollbackAvailable(bool deferred, bool overflow)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = TextDeclaration(8, "x");
        var target = TextDeclaration(4, "x");
        await using (var seed = await Builder(storage).AddDatabase(source).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync(overflow ? "INSERT INTO items VALUES('abcdef')" : "INSERT INTO items VALUES('a     ')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        int joint = 0;
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = _ => joint++;
            return new RecordingWorker(owner);
        });
        builder.AddDatabase("typed", database => { database.Schema(target); database.RunMigrationsInline = !deferred; });
        // Act
        if (overflow && !deferred)
        { await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync(guard.Token)); }
        else
        {
            await using var migrated = await builder.BuildAsync(guard.Token);
            var database = await migrated.OpenDatabaseAsync("typed", guard.Token);
            await WaitDoneAsync(database, guard.Token);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe((overflow ? source : target).Compile().Hash);
            await using var reader = await database.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT Code FROM items", guard.Token)).Single()[0].ShouldBe(overflow ? "abcdef" : "a   ");
            storage = storage.CaptureDurableImages();
        }
        // Assert
        joint.ShouldBeGreaterThan(0);
        await using var rollback = await Builder(storage.CaptureDurableImages()).AddDatabase(source).BuildAsync(guard.Token);
        var old = await rollback.OpenDatabaseAsync("typed", guard.Token);
        old.Catalog.PendingMigration.ShouldBeNull();
        old.OfflineError.ShouldBeNull();
        old.Catalog.SchemaState!.ContentHash.ShouldBe(source.Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint planning: indexed dropped columns refuse permanently with provisioning code")]
    public async Task ApplyAsync_PlanningDependency_ShouldReportPolicyRefusal()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE INDEX adhoc ON items(Value)", cancellationToken: guard.Token);
        var target = ChangeItems(Target().Compile(), columns: [new("Id", DatabaseType.Int64, false)], indexes: []);
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        database.Provisioning!.FailureCode.ShouldBe("CS03105");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.OfflineError.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint metadata: Build transfers definition exclusion with the table reservation")]
    public async Task ApplyAsync_MetadataHandoff_ShouldAlreadyExcludeDefinitionLease()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        bool observed = false;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "Handoff") { return; }
            // Exercise the exact interlock gap before the worker can enter metadata.
            using var conflicting = database.DdlInterlock.EnterMetadata(database.Name, definition: true);
            observed = true;
        };
        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));
        // Assert
        observed.ShouldBeFalse();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(Target().Compile(), guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint names: session CREATE cannot steal an unpublished table name")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_NewTableName_ShouldRefuseSessionCreateAndComplete(bool selectInto)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var target = Compiled(Target().Compile().Tables.Append(new CompiledSchemaTable("audit", "audit", [new("id", DatabaseType.Int32, true)], null, [], [])).ToArray());
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, true);
        await entered.Task.WaitAsync(guard.Token);
        try
        {
            // Act
            await using var session = await database.CreateSessionAsync(guard.Token);
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                selectInto ? "CREATE TABLE AUDIT AS SELECT Id AS id FROM other" : "CREATE TABLE AUDIT(id INT)", cancellationToken: guard.Token));
            // Assert
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03404");
            release.Set();
            await WaitDoneAsync(database, guard.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        }
        finally { release.Set(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint publication: unknown commit outcome keeps trees and reports unconfirmed")]
    public async Task ApplyAsync_UnconfirmedPublication_ShouldReportUnknownAndRecoverNew()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("joint-unconfirmed", new() { StorageStrategy = strategy, Durability = StorageCommitDurability.Grouped });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Source().Compile(), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "Publishing")
            {
                database.CatalogStorage.OnCommitPending = () =>
                {
                    database.CatalogStorage.FlushPendingCommits();
                    throw new NotSupportedException("injected unconfirmed publication");
                };
            }
        };
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03403");
        failure.Message.ShouldContain("unconfirmed");
        failure.Message.ShouldNotContain("unchanged");
        database.Provisioning!.FailureCode.ShouldBe("CS03403");
        database.IsOffline.ShouldBeTrue();
        var reopened = await engine.OpenDatabaseAsync("typed", guard.Token);
        reopened.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        reopened.Catalog.PendingMigration.ShouldBeNull();
        await using var reader = await reopened.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(1L);
    }

    private static SqlSchema TextDeclaration(int width, string defaultValue) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<TextFixRow>("items", table => { table.Column(row => row.Code, $"VARCHAR({width})"); table.Default(row => row.Code, defaultValue); });
    });

    private sealed record TextFixRow(string Code);
}
