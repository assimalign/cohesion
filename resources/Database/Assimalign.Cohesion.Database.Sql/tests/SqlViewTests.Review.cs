using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Views: correlated scalar output retains its enclosing column facets")]
    public async Task Execute_CorrelatedScalarMetadata_ShouldUseChildBindingScope()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-correlated-facets", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (amount DECIMAL(5,2))");
        await ExecuteAsync(session, "CREATE TABLE u (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1.25)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (1)");
        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT (SELECT t.amount FROM u) AS y FROM t");
        var metadata = await RowsAsync(session, "SELECT NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        var rows = await RowsAsync(session, "SELECT y FROM v");
        // Assert
        metadata.ShouldHaveSingleItem().ShouldBe([5L, 2L]);
        rows.ShouldHaveSingleItem().ShouldBe([1.25m]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: parameters and table targets report the view diagnostic")]
    public async Task Execute_InvalidViewTargets_ShouldReportViewCode()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-errors", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var parameter = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE VIEW v AS SELECT @p FROM t"));
        var table = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP VIEW t"));
        // Assert
        parameter.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03033");
        table.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03033");
        table.Message.ShouldBe("The target is a table, not a view.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: DROP RESTRICT names both target and dependent")]
    public async Task Execute_DropRestrictedView_ShouldNameTargetAndDependent()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-drop-message", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW target_v AS SELECT id FROM t");
        await ExecuteAsync(session, "CREATE VIEW dependent_v AS SELECT id FROM target_v");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP VIEW target_v RESTRICT"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        error.Message.ShouldBe("Cannot drop 'dbo.target_v' because other objects depend on it: view 'dbo.dependent_v' depends on it.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: COLUMNS survives a trailing ordinal change after a transaction snapshot")]
    public async Task Execute_SnapshotThenDropColumn_ShouldReadFrozenMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-snapshot-drop", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE t (spare INT, id VARCHAR(9))");
        await ExecuteAsync(writer, "CREATE VIEW v AS SELECT id FROM t");
        await ExecuteAsync(reader, "BEGIN");
        await RowsAsync(reader, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES");
        // Act
        await ExecuteAsync(writer, "ALTER TABLE t DROP COLUMN spare");
        var rows = await RowsAsync(reader, "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        await ExecuteAsync(reader, "COMMIT");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(["CHARACTER VARYING", 9L]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: added columns cannot capture existing references")]
    [InlineData("CREATE VIEW v AS SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.k = x)", "ALTER TABLE u ADD COLUMN x INT")]
    [InlineData("CREATE VIEW v AS SELECT x AS id FROM t JOIN u ON t.id = u.k", "ALTER TABLE u ADD COLUMN x INT")]
    public async Task Execute_AddColumn_ShouldRetainBoundReferences(string definition, string change)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bound-columns", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, x INT)");
        await ExecuteAsync(session, "CREATE TABLE u (k INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1,1), (2,2)");
        await ExecuteAsync(session, "INSERT INTO u VALUES (1)");
        await ExecuteAsync(session, definition);
        // Act
        await ExecuteAsync(session, change);
        var rows = await RowsAsync(session, "SELECT id FROM v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1]);
        database.Catalog.Views.Single().Definition.ShouldContain("t.x", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: index replacement never changes cached joins")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ReplaceIndex_ShouldRetainJoinResults(bool warm)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-index", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, x INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1,99)");
        await ExecuteAsync(session, "CREATE INDEX ix ON b (id)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT a.id FROM a JOIN b ON a.id = b.id");
        if (warm) { (await RowsAsync(session, "SELECT * FROM v")).ShouldHaveSingleItem().ShouldBe([1]); }
        // Act
        await ExecuteAsync(session, "DROP INDEX ix ON b");
        await ExecuteAsync(session, "CREATE INDEX ix ON b (x)");
        var rows = await RowsAsync(session, "SELECT * FROM v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1]);
        (await RowsAsync(session, "SELECT a.id FROM a JOIN b ON a.id = b.id")).ShouldHaveSingleItem().ShouldBe([1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: row counts use the current execution")]
    [InlineData("LIMIT read_limit(0)", 1, 2)]
    [InlineData("OFFSET read_limit(0)", 2, 1)]
    [InlineData("FETCH FIRST read_limit(0) ROWS ONLY", 1, 2)]
    public async Task Execute_VolatileWindow_ShouldEvaluateOncePerRead(string window, int firstCount, int nextCount)
    {
        // Arrange
        int count = 1;
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("view-counts");
        builder.Functions.Add(SqlScalarFunction.Create("read_limit", (int _) => { calls++; return count; }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t ORDER BY id " + window);
        calls.ShouldBe(0);
        (await RowsAsync(session, "SELECT * FROM v")).Count.ShouldBe(firstCount);
        calls.ShouldBe(1);
        // Act
        count = 2;
        var rows = await RowsAsync(session, "SELECT * FROM v");
        // Assert
        rows.Count.ShouldBe(nextCount);
        calls.ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id " + window)).Count.ShouldBe(nextCount);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: complete decimal facets survive scalar projections")]
    [InlineData("(SELECT CAST(id AS DECIMAL(5,2)) FROM t)", "(SELECT CAST(id AS DECIMAL(6,3)) FROM t)")]
    [InlineData("f(id)", "CAST(id AS DECIMAL(6,3))")]
    [InlineData("CASE WHEN id = 1 THEN CAST(id AS DECIMAL(5,2)) ELSE CAST(id AS DECIMAL(5,2)) END", "CASE WHEN id = 1 THEN CAST(id AS DECIMAL(6,3)) ELSE CAST(id AS DECIMAL(6,3)) END")]
    [InlineData("COALESCE(CAST(id AS DECIMAL(5,2)), CAST(id AS DECIMAL(5,2)))", "COALESCE(CAST(id AS DECIMAL(6,3)), CAST(id AS DECIMAL(6,3)))")]
    [InlineData("NULLIF(CAST(id AS DECIMAL(5,2)), CAST(0 AS DECIMAL(9,4)))", "NULLIF(CAST(id AS DECIMAL(6,3)), CAST(0 AS DECIMAL(9,4)))")]
    public async Task Execute_DecimalOutput_ShouldFreezePrecisionAndScale(string projection, string replacement)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-decimal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        await ExecuteAsync(session, "CREATE FUNCTION f(x INT) RETURNS DECIMAL(5,2) IMMUTABLE RETURN CAST(x AS DECIMAL(5,2))");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + projection + " AS y FROM t");
        // Act
        var rows = await RowsAsync(session, "SELECT NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT " + replacement + " AS y FROM t"));
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([5L, 2L]);
        error.Diagnostics[0].Code.ShouldBe("CS03033");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: repeated grouped expressions retain every overload")]
    [InlineData("ABS(id)", "ABS(id)")]
    [InlineData("CAST(ABS(id) AS INT)", "CAST(ABS(id) AS INT)")]
    [InlineData("ABS(id) = 1", "ABS(id) = 1")]
    [InlineData("ABS(id)", "ABS(id), ABS(id)")]
    [InlineData("CAST(ABS(id) AS INT)", "ABS(id)")]
    public async Task Execute_GroupedExpression_ShouldPersistEveryCall(string projection, string keys)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-group-keys", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + projection + " AS n FROM t GROUP BY " + keys);
        var actual = await RowsAsync(session, "SELECT n FROM v ORDER BY n");
        var expected = await RowsAsync(session, "SELECT " + projection + " AS n FROM t GROUP BY " + keys + " ORDER BY n");
        // Assert
        actual.Select(row => row[0]).ShouldBe(expected.Select(row => row[0]));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: altering referenced column facets names the dependent view")]
    [InlineData("INT", "BIGINT")]
    [InlineData("VARCHAR(3)", "VARCHAR(4)")]
    [InlineData("DECIMAL(5,2)", "DECIMAL(6,2)")]
    [InlineData("DECIMAL(5,2)", "DECIMAL(5,3)")]
    public async Task Execute_AlterColumnFacets_ShouldRefuseDependentView(string original, string changed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-alter", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (x " + original + ", spare VARCHAR(3))");
        await ExecuteAsync(session, "CREATE VIEW dependent_view AS SELECT x FROM t");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ALTER COLUMN x " + changed));
        await ExecuteAsync(session, "ALTER TABLE t ALTER COLUMN spare VARCHAR(4)");
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra INT");
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        error.Message.ShouldContain("view 'dbo.dependent_view'", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: output aliases never become input dependencies")]
    [InlineData("spare")]
    [InlineData("ABS(spare)")]
    public async Task Execute_OrderingAlias_ShouldAllowDroppingUnusedInput(string ordering)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-alias-dependencies", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, spare INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1,99)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id AS spare FROM t ORDER BY " + ordering);
        // Act
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN spare");
        var rows = await RowsAsync(session, "SELECT * FROM v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: any failed binding leaves open, metadata, unrelated reads and drop usable")]
    public async Task Open_IncompatibleNativeArgument_ShouldIsolateFailedView()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var initial = SqlDatabaseEngine.CreateBuilder("view-invalid-body");
        initial.Options.StorageStrategy = storage;
        initial.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using (var engine = await initial.BuildAsync(CancellationToken.None))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE VIEW v AS SELECT CAST(UPPER(native_value(id)) AS VARCHAR(7)) AS n FROM t");
        }
        var changed = SqlDatabaseEngine.CreateBuilder("view-invalid-body");
        changed.Options.StorageStrategy = storage.CaptureDurableImages();
        changed.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value));
        // Act
        await using var reopened = await changed.BuildAsync(CancellationToken.None);
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var rows = await RowsAsync(reader, "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT * FROM v"));
        await ExecuteAsync(reader, "DROP VIEW v");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(["CHARACTER VARYING", 7L]);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01006");
        (await RowsAsync(reader, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem().ShouldBe([3L]);
        restored.Catalog.Views.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: a recorded column identity is checked on every rebind")]
    public async Task Execute_ChangedRecordedColumn_ShouldRefuseRead()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-column-pins", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, spare INT)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        var previous = database.Catalog.Views.Single();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var replacement = new SqlCatalogView(previous.Schema, previous.Name, previous.Definition, previous.Columns,
            previous.TableDependencies, [SqlObjectIdentity.Column(table.ObjectId, "spare")], previous.FunctionCalls);
        await database.Catalog.CreateViewAsync(replacement, true, CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT * FROM v"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03033");
    }
}
