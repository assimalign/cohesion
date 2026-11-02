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
    [Fact(DisplayName = "Cohesion Test [Sql] - VARCHAR TYPE: default and NOT NULL changes defer as one joint plan")]
    public async Task ApplyAsync_VarcharDefaultAndNotNull_ShouldDeferAndPublishTogether()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = TextDeclaration(100, "x").Compile();
        var table = source.Tables.Single();
        var target = Compiled(new CompiledSchemaTable(table.Name, table.RowType,
            table.Columns.Select(column => column with { CustomType = "VARCHAR(50)", IsNullable = false, DefaultLiteral = "'y'" }).ToArray(),
            table.PrimaryKey, table.Indexes, table.Constraints));
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES('old')", cancellationToken: guard.Token);

        // Act: this is Build's deferred entry; the old duplicate-step identity failed at handoff.
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, true);
        await WaitDoneAsync(database, guard.Token);

        // Assert
        database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        Table(database).FindColumn("Code")!.IsNullable.ShouldBeFalse();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        await writer.ExecuteAsync("INSERT INTO items DEFAULT VALUES", cancellationToken: guard.Token);
        (await RowsAsync(writer, "SELECT Code FROM items ORDER BY Code", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { "old", "y" });
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(NULL)", cancellationToken: guard.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Native domain facets: inline Build uses the engine's TYPE classification for defaults")]
    public async Task BuildAsync_NativeDomainNarrowingAndDefault_ShouldUseJointTarget()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SqlSchema Declare(string value) => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<TextFixRow>("items", table => { table.Column(row => row.Code, "native_code"); table.Default(row => row.Code, value); });
        });
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = Declare("x");
        var seedBuilder = Builder(storage);
        seedBuilder.Types.Add(SqlType.CreateDomain("native_code", SqlType.VarChar(8)));
        await using (var seed = await seedBuilder.AddDatabase(source).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES('ok')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var target = Declare("y");
        var builder = Builder(storage);
        builder.Types.Add(SqlType.CreateDomain("native_code", SqlType.VarChar(4)));

        // Act
        // Domain availability must hold before Build accepts work, so its physical
        // bound change runs inline rather than leaving the old incompatible domain live.
        await using var migrated = await builder.AddDatabase("typed", db =>
        { db.Schema(target); db.RunMigrationsInline = true; }).BuildAsync(guard.Token);
        var result = await migrated.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(result, guard.Token);

        // Assert
        result.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        Table(result).FindColumn("Code")!.Type.MaxLength.ShouldBe(4);
        result.Catalog.SchemaState!.ContentHash.ShouldBe(target.Compile().Hash);
        await using var reader = await result.CreateSessionAsync(guard.Token);
        await reader.ExecuteAsync("INSERT INTO items DEFAULT VALUES", cancellationToken: guard.Token);
        (await RowsAsync(reader, "SELECT Code FROM items ORDER BY Code", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { "ok", "y" });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Non-TYPE Build: changed defaults refuse online and the old application opens")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_NonTypeDefaultChange_ShouldKeepOldApplicationAvailable(bool notNull)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var source = NumberDeclaration(nullable: true, precision: 10, defaultValue: "1");
        var target = NumberDeclaration(nullable: !notNull, precision: 5, defaultValue: "2");
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(source).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        SqlDatabase? observed = null;
        var builder = Builder(storage);
        builder.AddWorker(owner => { owner.BeforeProvisioning = database => observed = database; return new RecordingWorker(owner); });
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.AddDatabase(target).BuildAsync(guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        observed.ShouldNotBeNull().IsOffline.ShouldBeFalse();
        observed.Catalog.PendingMigration.ShouldBeNull();
        await using var rollback = await Builder(storage.CaptureDurableImages()).AddDatabase(source).BuildAsync(guard.Token);
        var old = await rollback.OpenDatabaseAsync("typed", guard.Token);
        old.Catalog.PendingMigration.ShouldBeNull();
        old.Catalog.SchemaState!.ContentHash.ShouldBe(source.Compile().Hash);
        old.SchemaProvisioner.CatalogWalkCount.ShouldBe(0);
        await using var reader = await old.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Code FROM items", guard.Token)).Single()[0].ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - TYPE with defaults: VARCHAR narrowing carries the complete target default")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_VarcharTypeAndDefault_ShouldPublishTogether(bool inline)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = TextDeclaration(100, "x");
        var target = TextDeclaration(50, "y");
        await using (var seed = await Builder(storage).AddDatabase(source).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES('old')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        // Act
        await using var migrated = await Builder(storage).AddDatabase("typed", db => { db.Schema(target); db.RunMigrationsInline = inline; }).BuildAsync(guard.Token);
        var result = await migrated.OpenDatabaseAsync("typed", guard.Token);
        await WaitDoneAsync(result, guard.Token);
        // Assert
        result.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        result.Catalog.PendingMigration.ShouldBeNull();
        result.Catalog.SchemaState!.ContentHash.ShouldBe(target.Compile().Hash);
        await using var reader = await result.CreateSessionAsync(guard.Token);
        await reader.ExecuteAsync("INSERT INTO items DEFAULT VALUES", cancellationToken: guard.Token);
        (await RowsAsync(reader, "SELECT Code FROM items ORDER BY Code", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { "old", "y" });
    }

    private static SqlSchema NumberDeclaration(bool nullable, int precision, string defaultValue) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (nullable)
        { schema.Table<NullableDefaultRow>("items", table => { table.Column(row => row.Code, $"DECIMAL({precision},0)"); table.DefaultSql(row => row.Code, defaultValue); }); }
        else
        { schema.Table<RequiredDefaultRow>("items", table => { table.Column(row => row.Code, $"DECIMAL({precision},0)"); table.DefaultSql(row => row.Code, defaultValue); }); }
    });

    private sealed record NullableDefaultRow(decimal? Code);
    private sealed record RequiredDefaultRow(decimal Code);
}
