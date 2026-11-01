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
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record AuditRow(int Id);

    private static SqlCompiledSchema WithAudit() => Compiled(Target().Compile().Tables.Append(
        new CompiledSchemaTable("audit", "audit", [new("id", DatabaseType.Int32, true)], null, [], [])).ToArray());

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint planning: abandoned owned sequences still occupy the persisted relation namespace")]
    public async Task ApplyAsync_OrphanSequenceName_ShouldRefuseBeforeIntent()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        long epoch = database.TableReclamation.EnterStatement();
        try
        {
            var old = Table(database);
            var prepared = database.Catalog.ReserveStorageReplacement([old], null,
                (table, id) => new SqlCatalogTable(id, table.Schema, table.Name, table.Columns), guard.Token);
            await database.Catalog.CreateSequenceAsync("dbo", "audit", new(), guard.Token, prepared[0].ObjectId, "Id");
            database.Catalog.AbandonStorageReplacement(prepared);

            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(WithAudit(), guard.Token));

            // Assert
            failure.Diagnostics.ShouldContain(d => d.Code == "CS03105");
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            database.IsOffline.ShouldBeFalse();
        }
        finally { database.TableReclamation.ExitStatement(epoch); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint planning: every occupied relation name refuses before intent and permits rollback Build")]
    [InlineData("CREATE VIEW audit AS SELECT Id FROM other")]
    [InlineData("CREATE SEQUENCE audit")]
    public async Task BuildAsync_OccupiedRelation_ShouldRefuseOnlineAndReopenOld(string ddl)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync(ddl, cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        SqlDatabase? observed = null;
        var builder = Builder(storage);
        builder.AddWorker(owner => { owner.BeforeProvisioning = db => observed = db; return new RecordingWorker(owner); });

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
        {
            await using var unexpected = await builder.AddDatabase("typed", db => db.Schema(schema =>
            {
                schema.AllowDestructiveChanges();
                schema.Table<NewRow>("items", table => { table.Key(row => row.Id); table.Default(row => row.Value, 7L); table.Index(row => row.Value); table.Default(row => row.Added, 3); });
                schema.Table<OtherRow>("other", table => table.Column(row => row.Id));
                schema.Table<AuditRow>("audit", table => table.Column(row => row.Id));
            })).BuildAsync(guard.Token);
        });

        // Assert
        _output.WriteLine(failure.ToString());
        failure.Diagnostics.ShouldContain(d => d.Code == "CS03105");
        observed.ShouldNotBeNull().Catalog.PendingMigration.ShouldBeNull();
        observed.IsOffline.ShouldBeFalse();
        await using var rollback = await Builder(storage.CaptureDurableImages()).AddDatabase(Source()).BuildAsync(guard.Token);
        (await rollback.OpenDatabaseAsync("typed", guard.Token)).Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint names: sequence, view and table rename respect unpublished names")]
    [InlineData("CREATE SEQUENCE AUDIT")]
    [InlineData("ALTER TABLE scratch RENAME TO AUDIT")]
    [InlineData("CREATE VIEW AUDIT AS SELECT Id FROM other")]
    public async Task ApplyAsync_DeferredRelationCreation_ShouldReserveNameAndReopen(string ddl)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = await SeedAsync(guard.Token);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE scratch(id INT)", cancellationToken: guard.Token);
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        await database.SchemaProvisioner.ApplyAsync(WithAudit(), guard.Token, true);
        await entered.Task.WaitAsync(guard.Token);
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(ddl, cancellationToken: guard.Token));

            // Assert
            failure.Diagnostics.ShouldContain(d => d.Code == "CS03404");
            release.Set();
            await WaitDoneAsync(database, guard.Token);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(WithAudit().Hash);
            await using var reopen = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
            (await reopen.OpenDatabaseAsync("typed", guard.Token)).Catalog.SchemaState!.ContentHash.ShouldBe(WithAudit().Hash);
        }
        finally { release.Set(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint publication: catalog lock rechecks all relation kinds")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_LateCatalogCollision_ShouldRefuseOnline(bool view)
    {
        // Arrange: bypass session admission to exercise the last catalog guard independently.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(guard.Token);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "Publishing") { return; }
            if (view)
            {
                database.Catalog.CreateViewAsync(new SqlCatalogView("dbo", "audit", "SELECT Id FROM other",
                    [new SqlCatalogColumn("Id", new(DatabaseType.Int32))], [], [], []), cancellationToken: guard.Token).GetAwaiter().GetResult();
            }
            else { database.Catalog.CreateSequenceAsync("dbo", "audit", new(), guard.Token).GetAwaiter().GetResult(); }
        };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(WithAudit(), guard.Token));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.IsOffline.ShouldBeFalse();
        await using var reopen = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        (await reopen.OpenDatabaseAsync("typed", guard.Token)).Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Provisioning: successful skip and inline apply clear an earlier planning failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_SuccessAfterRefusal_ShouldClearHealthFailure(bool apply)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var source = Source().Compile();
        await database.ApplySchemaAsync(source, guard.Token);
        var refused = new SqlCompiledSchema(source.Format, source.Name, false, source.Types, [], source.Principals);
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(refused, guard.Token));
        database.Provisioning.ShouldNotBeNull();

        // Act
        var result = await database.SchemaProvisioner.ApplyAsync(apply ? Compiled(source.Tables.Append(
            new CompiledSchemaTable("audit", "audit", [new("id", DatabaseType.Int32, true)], null, [], [])).ToArray()) : source, guard.Token, true);

        // Assert
        result.WasAlreadyApplied.ShouldBe(!apply);
        database.Provisioning.ShouldBeNull();
        database.IsOffline.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint planning: lifecycle and corruption keep precedence over policy refusals")]
    [InlineData("disposed")]
    [InlineData("offline")]
    [InlineData("corrupt")]
    [InlineData("catalog-corrupt")]
    [InlineData("unsupported")]
    [InlineData("cancel")]
    public async Task ApplyAsync_PlanningLifecycleFailure_ShouldPreserveIdentity(string kind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "Planning") { return; }
            if (kind == "offline")
            { database.CatalogStorage.TakeOffline(StorageOfflineCause.PageWriteBackFailures, "test", new System.IO.IOException("test")); }
            else if (kind == "disposed") { throw new ObjectDisposedException("test"); }
            else if (kind == "corrupt") { throw new StorageCorruptionException((PageId)2, "test"); }
            else if (kind == "catalog-corrupt") { throw new SqlCatalogException("CS06059", "test"); }
            else if (kind == "unsupported") { throw new SqlUnsupportedQueryException("test"); }
            else { throw new OperationCanceledException(guard.Token); }
        };

        // Act
        var failure = await Should.ThrowAsync<Exception>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));

        // Assert
        _output.WriteLine(failure.ToString());
        if (kind == "disposed") { failure.ShouldBeOfType<ObjectDisposedException>(); }
        else if (kind == "cancel") { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
        else if (kind == "offline") { failure.ShouldBeOfType<DatabaseOfflineException>(); }
        else if (kind == "catalog-corrupt") { ((DatabaseException)failure).Diagnostics.ShouldContain(d => d.Code == "CS06059"); }
        else if (kind == "unsupported") { ((SqlSchemaMigrationException)failure).Diagnostics.ShouldContain(d => d.Code == "CS03105"); }
        else { ((DatabaseException)failure).Diagnostics.ShouldContain(d => d.Code == "CK06003"); }
        if (kind != "unsupported") { database.Provisioning?.FailureCode.ShouldNotBe("CS03105"); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Space probe: Windows directories include a trailing separator")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("C:\\data", "C:\\data\\")]
    [InlineData("\\\\server\\share", "\\\\server\\share\\")]
    [InlineData("\\\\server\\share\\db", "\\\\server\\share\\db\\")]
    [InlineData("C:\\mount\\volume", "C:\\mount\\volume\\")]
    public void NormalizeDirectory_WindowsVolumePaths_ShouldPreserveDirectory(string input, string expected)
    {
        // Arrange / Act / Assert
        SqlVolumeSpace.NormalizeDirectory(input).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Space probe: long Windows drive and UNC paths retain extended directory syntax")]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalizeDirectory_LongWindowsPath_ShouldUseExtendedSyntax(bool unc)
    {
        // Arrange
        string suffix = new('a', 270);
        string path = (unc ? @"\\server\share\" : @"C:\") + suffix;
        string expected = (unc ? @"\\?\UNC\server\share\" : @"\\?\C:\") + suffix + @"\";

        // Act / Assert
        SqlVolumeSpace.NormalizeDirectory(path).ShouldBe(expected);
        SqlVolumeSpace.NormalizeDirectory(expected).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Script generator: every ALTER rendering refusal retains the public exception contract")]
    [InlineData("VARCHAR(bogus)")]
    [InlineData("bad.domain.name")]
    public void Generate_InvalidAlterType_ShouldWrapLikeBase(string customType)
    {
        // Arrange
        var previous = new CompiledSchemaColumn("Value", DatabaseType.Int32, true);
        var desired = previous with { CustomType = customType };
        var operation = new SqlSchemaMigrationOperation(SqlSchemaMigrationOperationKind.AlterColumn,
            SqlSchemaMigrationSafety.Destructive, "Value", "items", column: desired, previousColumn: previous);
        var plan = new SqlSchemaMigrationPlan(null, "target", [operation]);

        // Act
        var failure = Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(plan));

        // Assert
        failure.InnerException.ShouldNotBeNull();
        failure.Message.ShouldContain("AlterColumn");
    }
}
