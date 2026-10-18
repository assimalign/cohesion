using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Views: aggregate modifiers survive persisted round trips")]
    [InlineData("COUNT(DISTINCT id)", "3")]
    [InlineData("COUNT(ALL id)", "4")]
    [InlineData("COUNT(id)", "4")]
    [InlineData("STRING_AGG(CAST(id AS TEXT), ',' ORDER BY id DESC)", "3,2,1,1")]
    [InlineData("LISTAGG(CAST(id AS TEXT), ',') WITHIN GROUP (ORDER BY id DESC)", "3,2,1,1")]
    public async Task Open_AggregateModifiers_ShouldPreserveResults(string aggregate, string expected)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("view-aggregates", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + aggregate + " AS n FROM t");
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("view-aggregates", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var rows = await RowsAsync(reader, "SELECT n FROM v");
        // Assert
        Convert.ToString(rows.ShouldHaveSingleItem()[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: invalid calls over ordering aliases fail before reading empty inputs")]
    public async Task Execute_InvalidOrderingAliasCall_ShouldFailAtCreateOnEmptyInput()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("view-alias-invalid");
        builder.Functions.Add(SqlScalarFunction.Create("alias_value", static (int value) => value));
        builder.Functions.Add(SqlScalarFunction.Create("alias_value", static (long value) => value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        const string body = "SELECT CAST(id AS TEXT) AS n FROM t WHERE id < 0 ORDER BY alias_value(n)";
        // Act
        var inline = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, body));
        var view = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE VIEW invalid AS " + body));
        // Assert
        inline.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE006");
        view.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE006");
        database.Catalog.Views.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: ordering aliases persist the overload selected in the output scope")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_OrderingAliasOverload_ShouldPersistResolvedSignature(bool grouped)
    {
        // Arrange
        var selected = SqlScalarFunction.Create("alias_value", static (int value) => value);
        var builder = SqlDatabaseEngine.CreateBuilder("view-alias-overload");
        builder.Functions.Add(selected);
        builder.Functions.Add(SqlScalarFunction.Create("alias_value", static (long value) => value + 100));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string projection = grouped ? "CAST(COUNT(*) AS INT)" : "id";
        string body = "SELECT " + projection + " AS n FROM t ORDER BY alias_value(n) DESC LIMIT 1";
        (await RowsAsync(session, body)).ShouldHaveSingleItem().ShouldBe([3]);
        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS " + body);
        var rows = await RowsAsync(session, "SELECT n FROM v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([3]);
        database.Catalog.Views.ShouldHaveSingleItem().FunctionCalls.ShouldContain(SqlDefinitionBinder.Key(selected));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: schema apply refuses destructive changes to referenced objects")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchema_ViewDependency_ShouldNameViewAndKeepSource(bool dropTable)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-schema", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var table = new CompiledSchemaTable("items", "Item",
            [new("id", Types.DatabaseType.Int32, false), new("value", Types.DatabaseType.Int32, false)], null, [], []);
        await database.ApplySchemaAsync(new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", true, [], [table], []), CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE VIEW dependent AS SELECT value FROM items");
        CompiledSchemaTable[] targets = dropTable ? [] :
            [new("items", "Item", [table.Columns[0]], null, [], [])];
        var target = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", true, [], targets, []);
        // Act
        var error = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, CancellationToken.None));
        // Assert
        error.Message.ShouldContain("dependent");
        database.Catalog.TryGetTable("dbo", "items", out var retained).ShouldBeTrue();
        retained.FindColumn("value").ShouldNotBeNull();
        database.Catalog.Views.ShouldHaveSingleItem().Name.ShouldBe("dependent");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: a changed native return type cannot silently retype persisted output")]
    public async Task Open_ChangedNativeReturnType_ShouldKeepFrozenViewContract()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var initial = SqlDatabaseEngine.CreateBuilder("native-contract");
        initial.Options.StorageStrategy = storage;
        initial.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value));
        await using (var engine = await initial.BuildAsync(CancellationToken.None))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE VIEW v AS SELECT native_value(id) AS n FROM t");
        }
        var changed = SqlDatabaseEngine.CreateBuilder("native-contract");
        changed.Options.StorageStrategy = storage.CaptureDurableImages();
        changed.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Act
        await using var reopened = await changed.BuildAsync(CancellationToken.None);
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT n FROM v"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE009");
        restored.Catalog.Views.ShouldHaveSingleItem().Columns[0].Type.Type.ShouldBe(Types.DatabaseType.Int32);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: captured COLUMNS survives later view table and function drops")]
    public async Task Execute_CapturedViewMetadata_ShouldRetainItsSourceImage()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("metadata-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE t (id INT NOT NULL)");
        await ExecuteAsync(writer, "CREATE FUNCTION f(x INT) RETURNS INT IMMUTABLE RETURN x + 1");
        await ExecuteAsync(writer, "CREATE VIEW v AS SELECT id, f(id) AS n FROM t");
        await ExecuteAsync(reader, "BEGIN");
        await RowsAsync(reader, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES");
        // Act - first bind of this retained image happens after the current definitions disappeared.
        await ExecuteAsync(writer, "DROP VIEW v");
        await ExecuteAsync(writer, "DROP TABLE t");
        await ExecuteAsync(writer, "DROP FUNCTION f(INT)");
        var rows = await RowsAsync(reader, "SELECT COLUMN_NAME, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v' ORDER BY ORDINAL_POSITION");
        await ExecuteAsync(reader, "COMMIT");
        // Assert
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(["id", "NO"]);
        rows[1].ShouldBe(["n", "YES"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: replacement retains domain identities and type modifiers")]
    public async Task Execute_ReplacingTypeFacets_ShouldKeepOriginalContract()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("facets", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE DOMAIN first_domain AS INT");
        await ExecuteAsync(session, "CREATE DOMAIN second_domain AS INT");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT CAST(id AS first_domain) AS n, CAST(id AS VARCHAR(3)) COLLATE binary AS label FROM t");
        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT CAST(id AS second_domain) AS n, CAST(id AS VARCHAR(3)) COLLATE binary AS label FROM t"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT CAST(id AS first_domain) AS n, CAST(id AS VARCHAR(4)) COLLATE binary AS label FROM t"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT CAST(id AS first_domain) AS n, CAST(id AS VARCHAR(3)) COLLATE case_insensitive AS label FROM t"));
        database.Catalog.Views.ShouldHaveSingleItem().Columns[0].DomainName.ShouldBe("first_domain");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: USING dependencies and merged star types are retained")]
    public async Task Execute_UsingJoin_ShouldFreezeMergedOutputAndProtectJoinKeys()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-views", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, label TEXT)");
        await ExecuteAsync(session, "CREATE TABLE b (id BIGINT, extra INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1, 'one')");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1, 9)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT * FROM a JOIN b USING (id)");
        await ExecuteAsync(session, "CREATE VIEW keys_only AS SELECT label FROM a JOIN b USING (id)");
        // Act
        await ExecuteAsync(session, "ALTER TABLE a ADD later INT");
        var rows = await RowsAsync(session, "SELECT * FROM v");
        var left = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE a DROP COLUMN id"));
        var right = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE b DROP COLUMN id"));
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1L, "one", 9]);
        left.Message.ShouldContain("view");
        right.Message.ShouldContain("view");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: replacement retires obsolete transitive dependencies")]
    public async Task Execute_ReplacedDependency_ShouldFollowCurrentViewGraph()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dependency-refresh", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE replacement (id INT)");
        await ExecuteAsync(session, "INSERT INTO replacement VALUES (9)");
        await ExecuteAsync(session, "CREATE VIEW base_v AS SELECT id FROM t");
        await ExecuteAsync(session, "CREATE VIEW outer_v AS SELECT id FROM base_v");
        await RowsAsync(session, "SELECT * FROM outer_v");
        // Act
        await ExecuteAsync(session, "CREATE OR REPLACE VIEW base_v AS SELECT id FROM replacement");
        await ExecuteAsync(session, "DROP TABLE t");
        var rows = await RowsAsync(session, "SELECT id FROM outer_v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([9]);
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP TABLE replacement"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: one bound plan per catalog generation and frozen COLUMNS nullability")]
    public async Task Plan_RepeatedViewRead_ShouldReuseBodyUntilCatalogChanges()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cache", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        SqlPlan Body() => new SqlPlanner(database.Catalog, null, database.Definitions.Functions)
            .Plan(SqlQueryRequest.FromSql("SELECT * FROM v").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>().Bindings[0].Source.Derived!.Query;
        // Act
        var first = Body();
        var repeated = Body();
        await ExecuteAsync(session, "ALTER TABLE t ALTER COLUMN id SET NOT NULL");
        var changed = Body();
        var nullable = await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        // Assert
        ReferenceEquals(first, repeated).ShouldBeTrue();
        ReferenceEquals(first, changed).ShouldBeFalse();
        nullable.ShouldHaveSingleItem().ShouldBe(["YES"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: added overloads never replace persisted calls")]
    public async Task Execute_AddedOverload_ShouldKeepRecordedFunctionIdentity()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("pins", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE FUNCTION choose(x BIGINT) RETURNS INT IMMUTABLE RETURN 10");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT choose(id) AS n FROM t");
        await RowsAsync(session, "SELECT * FROM v");
        // Act
        await ExecuteAsync(session, "CREATE FUNCTION choose(x INT) RETURNS INT IMMUTABLE RETURN 20");
        var persisted = await RowsAsync(session, "SELECT n FROM v LIMIT 1");
        var current = await RowsAsync(session, "SELECT choose(id) FROM t LIMIT 1");
        // Assert
        persisted.ShouldHaveSingleItem().ShouldBe([10]);
        current.ShouldHaveSingleItem().ShouldBe([20]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: statement and transaction snapshots hide later commits")]
    public async Task Execute_CachedView_ShouldUseCurrentStatementSnapshot()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("snapshots", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(reader);
        await ExecuteAsync(reader, "CREATE VIEW v AS SELECT id FROM t");
        await ExecuteAsync(reader, "BEGIN");
        await RowsAsync(reader, "SELECT * FROM v");
        // Act
        await ExecuteAsync(writer, "INSERT INTO t VALUES (4)");
        var old = await RowsAsync(reader, "SELECT COUNT(*) FROM v");
        await ExecuteAsync(reader, "COMMIT");
        var fresh = await RowsAsync(reader, "SELECT COUNT(*) FROM v");
        // Assert
        old.ShouldHaveSingleItem().ShouldBe([3L]);
        fresh.ShouldHaveSingleItem().ShouldBe([4L]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: every CREATE crash boundary restores absence or a complete view")]
    public async Task Open_CreateCrashPoints_ShouldRestoreAtomicViewDefinition()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("crash", new() { StorageStrategy = storage });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        using var points = storage.RecordCrashPoints();
        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t WHERE id = 2");
        points.Dispose();
        int absent = 0, present = 0;
        // Assert
        for (int point = 0; point < points.Count; point++)
        {
            await using var reopened = SqlDatabaseEngine.Create("restored", new() { StorageStrategy = points.Open(point) });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            if (!restored.Catalog.TryGetView("dbo", "v", out _)) { absent++; continue; }
            present++;
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            (await RowsAsync(reader, "SELECT id FROM v")).ShouldHaveSingleItem().ShouldBe([2]);
        }
        absent.ShouldBeGreaterThan(0);
        present.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: declared-script receipts and ownership survive every crash boundary")]
    public async Task Build_ViewScriptCrashPoints_ShouldResumeWithoutReplayingCreate()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = SqlDatabaseEngine.Create("script", new() { StorageStrategy = storage }))
        { await seed.CreateDatabaseAsync("db", CancellationToken.None); }
        storage = storage.CaptureDurableImages();
        using var points = storage.RecordCrashPoints();
        await using (var original = await Builder(storage).BuildAsync(CancellationToken.None)) { }
        points.Dispose();
        // Act / Assert
        for (int point = 0; point < points.Count; point++)
        {
            await using var engine = await Builder(points.Open(point)).BuildAsync(CancellationToken.None);
            var database = await engine.OpenDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            (await RowsAsync(session, "SELECT id FROM v")).ShouldHaveSingleItem().ShouldBe([1]);
            var receipt = database.Catalog.Scripts.ShouldHaveSingleItem();
            receipt.CompletedStatements.ShouldBe(5);
            receipt.StatementObjects[1].ShouldStartWith("V:");
            receipt.StatementObjects[2].ShouldStartWith("V:");
            receipt.StatementObjects[3].ShouldStartWith("V:");
            database.Catalog.Views.ShouldHaveSingleItem().Owner.ShouldBe(DatabaseObjectOwner.Schema);
        }
        SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy strategy)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("script");
            builder.Options.StorageStrategy = strategy;
            builder.AddDatabase("db", database => database.Script("views", "CREATE TABLE t (id INT); CREATE VIEW v AS SELECT id FROM t; CREATE OR REPLACE VIEW v AS SELECT id FROM t; DROP VIEW IF EXISTS absent; INSERT INTO t VALUES (1);"));
            return builder;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: text larger than one catalog page is refused before publication")]
    public async Task Execute_OversizedView_ShouldLeaveNoPartialRecord()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("large", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        ulong before = database.Catalog.DdlGeneration;
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE VIEW v AS SELECT '" + new string('x', 9000) + "' AS n FROM t"));
        // Assert
        error.Message.ShouldContain("8,092", Case.Sensitive);
        database.Catalog.DdlGeneration.ShouldBe(before);
        database.Catalog.Views.ShouldBeEmpty();
    }
}
