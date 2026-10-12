using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Scripts refuse typed object changes before Build changes the database")]
    [InlineData("ALTER TABLE typed_rows ADD COLUMN extra INT", "typed_rows")]
    [InlineData("DROP TABLE typed_rows", "typed_rows")]
    [InlineData("DROP INDEX PK_typed_rows ON typed_rows", "PK_typed_rows")]
    [InlineData("DROP INDEX IX_typed_rows_Id ON typed_rows", "IX_typed_rows_Id")]
    public async Task BuildAsync_ScriptChangesTypedObject_ShouldRefuseBeforeAnyEffects(string sql, string objectName)
    {
        // Arrange: an existing database must keep its exact catalog and rows on refusal.
        var storage = new CrashCaptureSqlStorageStrategy();
        var seed = SqlDatabaseEngine.CreateBuilder("sql-script");
        seed.Options.StorageStrategy = storage;
        await using (var engine = await seed.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, "CREATE TABLE preserved (id INT)");
            await ExecuteAsync(session, "INSERT INTO preserved VALUES (42)");
        }
        var images = storage.CaptureDurableImages();
        var beforeCatalog = images.GetDurableImage("functions", catalog: true);
        var beforeData = images.GetDurableImage("functions");
        var builder = SqlDatabaseEngine.CreateBuilder("sql-script");
        builder.Options.StorageStrategy = images;
        builder.AddDatabase("functions", database =>
        {
            database.Schema(schema => schema.Table<ScriptRow>("typed_rows", table =>
            {
                table.Key(row => row.Id);
                table.Index(row => row.Id);
            }));
            database.Script("001", "INSERT INTO preserved VALUES (99); " + sql);
        });

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync(CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("COHSQLS005");
        failure.Message.ShouldContain(objectName);
        failure.Message.ShouldContain("declare the change in the typed schema");
        var afterCatalog = images.GetDurableImage("functions", catalog: true);
        var afterData = images.GetDurableImage("functions");
        afterCatalog.Data.ShouldBe(beforeCatalog.Data);
        afterCatalog.Journal.ShouldBe(beforeCatalog.Journal);
        afterCatalog.Backup.ShouldBe(beforeCatalog.Backup);
        afterData.Data.ShouldBe(beforeData.Data);
        afterData.Journal.ShouldBe(beforeData.Journal);
        afterData.Backup.ShouldBe(beforeData.Backup);
        var reopen = SqlDatabaseEngine.CreateBuilder("sql-script");
        reopen.Options.StorageStrategy = images.CaptureDurableImages();
        await using var reopened = await reopen.BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);
        database.Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("preserved");
        database.Catalog.SchemaState.ShouldBeNull();
        database.Catalog.Scripts.ShouldBeEmpty();
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT id FROM preserved")).ShouldBe([[42]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Scripts retain their own altered tables across typed schema evolution")]
    public async Task BuildAsync_ScriptAltersOwnTable_ShouldSurviveTypedEvolution()
    {
        // Arrange
        const string sql = "CREATE TABLE script_rows (id INT); ALTER TABLE script_rows ADD COLUMN extra INT; INSERT INTO script_rows VALUES (1, 2)";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var first = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None)) { }
        var builder = SqlDatabaseEngine.CreateBuilder("sql-script");
        builder.Options.StorageStrategy = storage.CaptureDurableImages();
        builder.AddDatabase("functions", database =>
        {
            database.Schema(schema => schema.Table<EvolvedScriptRow>("typed_rows", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Next);
            }));
            database.Script("001", sql);
        });

        // Act
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(session, "SELECT id, extra FROM script_rows")).ShouldBe([[1, 2]]);
        database.Catalog.TryGetTable("dbo", "typed_rows", out var typed).ShouldBeTrue();
        typed.FindColumn("Next").ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A later Build may alter a table created by an earlier script")]
    public async Task BuildAsync_LaterScriptAltersOwnTable_ShouldResumeAcrossBuilds()
    {
        // Arrange
        const string create = "CREATE TABLE script_rows (id INT); INSERT INTO script_rows VALUES (1)";
        const string alter = "ALTER TABLE script_rows ADD COLUMN extra INT; UPDATE script_rows SET extra = 2";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var first = await ScriptBuilder(storage, sql: create).BuildAsync(CancellationToken.None)) { }
        var secondStorage = storage.CaptureDurableImages();

        // Act: the new forward-only script runs after the typed table gains Next.
        await using (var second = await LaterBuilder(secondStorage).BuildAsync(CancellationToken.None)) { }
        await using var third = await LaterBuilder(secondStorage.CaptureDurableImages()).BuildAsync(CancellationToken.None);
        var database = await third.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(session, "SELECT id, extra FROM script_rows")).ShouldBe([[1, 2]]);
        database.Catalog.Scripts.Count.ShouldBe(2);
        database.Catalog.TryGetTable("dbo", "typed_rows", out var typed).ShouldBeTrue();
        typed.FindColumn("Next").ShouldNotBeNull();

        static SqlDatabaseEngineBuilder LaterBuilder(CrashCaptureSqlStorageStrategy strategy)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("sql-script");
            builder.Options.StorageStrategy = strategy;
            builder.AddDatabase("functions", database =>
            {
                database.Schema(schema => schema.Table<EvolvedScriptRow>("typed_rows", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Next);
                }));
                database.Script("001", create);
                database.Script("002", alter);
            });
            return builder;
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Scripts may index and write typed tables across Builds")]
    public async Task BuildAsync_ScriptIndexesTypedTable_ShouldPreserveTypedSnapshot()
    {
        // Arrange
        const string sql = "CREATE INDEX script_ix ON typed_rows(Id); INSERT INTO typed_rows VALUES (3)";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var first = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None)) { }

        // Act
        await using var engine = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql).BuildAsync(CancellationToken.None);
        var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(session, "SELECT Id FROM typed_rows")).ShouldBe([[3]]);
        database.Catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldContain("I:\"dbo\".\"typed_rows\".\"script_ix\"");
    }

    private sealed class EvolvedScriptRow
    {
        public int Id { get; set; }
        public int? Next { get; set; }
    }
}
