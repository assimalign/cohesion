using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed partial class SqlSchemaExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - SQL namespaces persist independently of tables and preserve their spelling")]
    public async Task ExecuteAsync_EmptyAndPopulatedNamespaces_ShouldSurviveRestart()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("namespaces", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
            database.Catalog.Namespaces[0].Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);

            // Act
            await ExecuteAsync(session, "CREATE SCHEMA empty_space");
            await ExecuteAsync(session, "CREATE SCHEMA sales");
            await ExecuteAsync(session, "CREATE TABLE Sales.t(id INT)");
            await ExecuteAsync(session, "INSERT INTO sales.t VALUES (7)");

            // Assert
            (await RowsAsync(session, "SELECT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't'"))
                .ShouldBe([["sales"]]);
            await using var schemas = (DatabaseResultSet)await ExecuteAsync(session, "SELECT * FROM INFORMATION_SCHEMA.SCHEMATA");
            schemas.Columns.Select(column => column.IsNullable).ShouldBe([false, false, true, true, true, true, true]);
        }
        await using var reopened = SqlDatabaseEngine.Create("namespaces-reopened", new() { StorageStrategy = storage.CaptureDurableImages() });
        var result = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        ulong generation = result.Catalog.DdlGeneration;
        await using var reader = await result.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME"))
            .ShouldBe([["dbo"], ["empty_space"], ["sales"]]);
        (await RowsAsync(reader, "SELECT id FROM SALES.t")).ShouldBe([[7]]);
        result.Catalog.DdlGeneration.ShouldBe(generation);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema existence failures use creation versus relation diagnostics")]
    [InlineData("CREATE TABLE missing.t(id INT)", "COHSQLE028")]
    [InlineData("CREATE TABLE IF NOT EXISTS missing.t(id INT)", "COHSQLE028")]
    [InlineData("DROP TABLE missing.t", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t ADD COLUMN c INT", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t DROP COLUMN c", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t ADD CONSTRAINT ck CHECK (id > 0)", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t DROP CONSTRAINT ck", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t RENAME TO renamed", "COHSQLE028")]
    [InlineData("ALTER TABLE missing.t ALTER COLUMN id SET DEFAULT 1", "COHSQLE028")]
    [InlineData("CREATE INDEX ix ON missing.t(id)", "COHSQLE028")]
    [InlineData("DROP INDEX ix ON missing.t", "COHSQLE028")]
    [InlineData("DROP INDEX IF EXISTS ix ON missing.t", "COHSQLE028")]
    [InlineData("SELECT * FROM missing.t", "COHSQLE014")]
    [InlineData("INSERT INTO missing.t VALUES (1)", "COHSQLE014")]
    [InlineData("UPDATE missing.t SET id = 1", "COHSQLE014")]
    [InlineData("DELETE FROM missing.t", "COHSQLE014")]
    [InlineData("DROP SCHEMA missing", "COHSQLE028")]
    [InlineData("DROP SCHEMA dbo CASCADE", "COHSQLE031")]
    [InlineData("DROP SCHEMA IF EXISTS DBO", "COHSQLE031")]
    public async Task ExecuteAsync_MissingOrProtectedNamespace_ShouldFailWithoutPublication(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-errors", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Virtual namespaces refuse CREATE SCHEMA, DROP SCHEMA and CREATE TABLE")]
    [InlineData("CREATE SCHEMA INFORMATION_SCHEMA")]
    [InlineData("CREATE SCHEMA IF NOT EXISTS cohesion_schema")]
    [InlineData("DROP SCHEMA information_schema CASCADE")]
    [InlineData("DROP SCHEMA IF EXISTS COHESION_SCHEMA")]
    [InlineData("CREATE TABLE INFORMATION_SCHEMA.foo(id INT)")]
    [InlineData("CREATE TABLE COHESION_SCHEMA.foo(id INT)")]
    public async Task ExecuteAsync_ReservedNamespace_ShouldRefuseMutation(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-reserved", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE021");
        database.Catalog.Tables.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Namespace guards are no-ops and DROP SCHEMA never drops a table called schema")]
    public async Task ExecuteAsync_ExistenceGuards_ShouldPreserveGenerationAndOrdinaryNames()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-guards", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE schema(authorization INT)");
        await ExecuteAsync(session, "CREATE SCHEMA sales");
        ulong generation = database.Catalog.DdlGeneration;

        // Act / Assert
        await ExecuteAsync(session, "CREATE SCHEMA IF NOT EXISTS SALES");
        await ExecuteAsync(session, "DROP SCHEMA IF EXISTS absent CASCADE");
        database.Catalog.DdlGeneration.ShouldBe(generation);
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE SCHEMA SALES")))
            .Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE029");
        await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, "DROP SCHEMA;"));
        database.Catalog.TryGetTable("dbo", "schema", out _).ShouldBeTrue();
        await ExecuteAsync(session, "DROP SCHEMA sales");
        database.Catalog.DdlGeneration.ShouldBe(generation + 1);
        await ExecuteAsync(session, "DROP TABLE IF EXISTS absent.t");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RESTRICT refuses nonempty namespaces with internal and external references")]
    [InlineData("")]
    [InlineData(" RESTRICT")]
    public async Task ExecuteAsync_Restrict_ShouldPreserveEveryDependentObject(string suffix)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-restrict", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP SCHEMA sales" + suffix));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE030");
        failure.Message.ShouldContain("because other objects depend on it", Case.Sensitive);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.Tables.Count.ShouldBe(4);
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO external_child VALUES (99)"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASCADE atomically retires internal tables and removes only external foreign keys")]
    public async Task ExecuteAsync_Cascade_ShouldKeepExternalTablesAndCleanIdentityAndIndexes()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("namespace-cascade", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            ulong generation = database.Catalog.DdlGeneration;
            var retired = database.Catalog.Tables.Where(table => table.Schema == "sales").Select(table => table.ObjectId).ToArray();
            var indexes = retired.SelectMany(database.Catalog.GetIndexes).ToArray();
            indexes.ShouldNotBeEmpty();
            foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeTrue(); }

            // Act
            await ExecuteAsync(session, "DROP SCHEMA SALES CASCADE");

            // Assert
            database.Catalog.DdlGeneration.ShouldBe(generation + 1);
            database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
            database.Catalog.Tables.Select(table => table.Name).Order().ShouldBe(["external_child", "external_parent"]);
            database.Catalog.GetIndexRegistrations().ShouldNotContain(registration => retired.Contains(registration.ObjectId));
            foreach (var index in indexes) { database.IndexManager.TryGetIndex(index.TableObjectId, index.Name, out _).ShouldBeFalse(); }
            database.Catalog.Tables.Single(table => table.Name == "external_child").Constraints.ShouldBeEmpty();
            await ExecuteAsync(session, "INSERT INTO external_child VALUES (99)");
            await ExecuteAsync(session, "DELETE FROM external_parent");
        }
        await using var reopened = SqlDatabaseEngine.Create("namespace-cascade-reopen", new() { StorageStrategy = storage.CaptureDurableImages() });
        var result = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await result.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT pid FROM external_child ORDER BY pid")).ShouldBe([[1], [99]]);
        await ExecuteAsync(reader, "CREATE SCHEMA sales");
        await ExecuteAsync(reader, "CREATE TABLE sales.parent(id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY)");
        await ExecuteAsync(reader, "INSERT INTO sales.parent DEFAULT VALUES");
        (await RowsAsync(reader, "SELECT id FROM sales.parent")).ShouldBe([[1]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASCADE adopts surviving children's CHECK and DEFAULT bindings")]
    public async Task ExecuteAsync_Cascade_ShouldRetainBoundExternalChildWithoutRebinding()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-bindings", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SCHEMA sales");
        await ExecuteAsync(session, "CREATE TABLE sales.parent(id INT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE child(pid INT DEFAULT (90 + 9), CONSTRAINT ck CHECK (pid > 0), CONSTRAINT fk FOREIGN KEY(pid) REFERENCES sales.parent(id))");
        await ExecuteAsync(session, "INSERT INTO sales.parent VALUES (1)");
        await ExecuteAsync(session, "INSERT INTO child VALUES (1)");
        database.Catalog.TryGetTable("dbo", "child", out var previous).ShouldBeTrue();
        database.Definitions.IsBound(previous).ShouldBeTrue();
        long binds = database.Definitions.BindCount;

        // Act
        await ExecuteAsync(session, "DROP SCHEMA sales CASCADE");

        // Assert
        database.Catalog.TryGetTable("dbo", "child", out var child).ShouldBeTrue();
        child.ShouldNotBeSameAs(previous);
        child.Constraints.ShouldHaveSingleItem().Name.ShouldBe("ck");
        database.Definitions.IsBound(child).ShouldBeTrue();
        database.Definitions.BindCount.ShouldBe(binds);
        await ExecuteAsync(session, "INSERT INTO child DEFAULT VALUES");
        database.Definitions.BindCount.ShouldBe(binds);
        (await RowsAsync(session, "SELECT pid FROM child ORDER BY pid")).ShouldBe([[1], [99]]);
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO child VALUES (-1)"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DROP SCHEMA honors namespace, contained-table and external-child compiled ownership")]
    [InlineData("namespace")]
    [InlineData("contained")]
    [InlineData("external")]
    public async Task ExecuteAsync_CompiledOwnership_ShouldRefuseCascadeBeforePublication(string owned)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-owned", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var column = new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false);
        if (owned == "namespace")
        {
            await database.Catalog.CreateSchemaAsync(new SqlCatalogNamespace("sales", DatabaseObjectOwner.Schema, "App"), CancellationToken.None);
        }
        else
        {
            await ExecuteAsync(session, "CREATE SCHEMA sales");
            var parent = await database.Catalog.ReserveTableAsync("sales", "parent", [column], ["id"], [],
                owner: owned == "contained" ? DatabaseObjectOwner.Schema : DatabaseObjectOwner.Adhoc,
                owningSchema: owned == "contained" ? "App" : null, cancellationToken: CancellationToken.None);
            await database.Catalog.PublishTableAsync(parent, [], [], cancellationToken: CancellationToken.None);
            if (owned == "external")
            {
                var child = await database.Catalog.ReserveTableAsync("dbo", "child", [column], [],
                    [new SqlCatalogConstraint("fk", SqlCatalogConstraintKind.Reference, ["id"], "sales", "parent", ["id"])],
                    DatabaseObjectOwner.Schema, "App", CancellationToken.None);
                await database.Catalog.PublishTableAsync(child, [], [], cancellationToken: CancellationToken.None);
            }
        }
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var failure = await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session, "DROP SCHEMA sales CASCADE"));

        // Assert
        failure.Message.ShouldContain("DROP SCHEMA", Case.Sensitive);
        failure.Message.ShouldContain("App", Case.Sensitive);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.Namespaces.ShouldContain(schema => schema.Name == "sales");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SCHEMATA shares the transaction catalog image with TABLES")]
    public async Task ExecuteAsync_Snapshot_ShouldRetainDroppedAndExcludeNewNamespaces()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(writer, "CREATE SCHEMA before");
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Act
        await ExecuteAsync(writer, "DROP SCHEMA before");
        await ExecuteAsync(writer, "CREATE SCHEMA after");

        // Assert
        (await RowsAsync(reader, "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME")).ShouldBe([["before"], ["dbo"]]);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME")).ShouldBe([["after"], ["dbo"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema DDL refuses explicit transactions and rejected syntax publishes nothing")]
    [InlineData("CREATE SCHEMA sales")]
    [InlineData("DROP SCHEMA sales CASCADE")]
    public async Task ExecuteAsync_ExplicitTransaction_ShouldRefuseSchemaDdl(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("namespace-transaction", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SCHEMA sales");
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Act
        var failure = await ExecuteAsync(session, sql);

        // Assert
        failure.Status.ShouldNotBe(DatabaseResultStatus.Success);
        failure.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == "COHSQLT003");
        await transaction.RollbackAsync(CancellationToken.None);
        database.Catalog.Namespaces.ShouldContain(schema => schema.Name == "sales");
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE SCHEMA sales");
        await ExecuteAsync(session, "CREATE TABLE external_parent(id INT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE sales.parent(id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE sales.child(pid INT REFERENCES parent(id), outside_id INT REFERENCES dbo.external_parent(id))");
        await ExecuteAsync(session, "CREATE INDEX ix_child ON sales.child(pid)");
        await ExecuteAsync(session, "CREATE TABLE external_child(pid INT REFERENCES sales.parent(id))");
        await ExecuteAsync(session, "INSERT INTO external_parent VALUES (1)");
        await ExecuteAsync(session, "INSERT INTO sales.parent DEFAULT VALUES");
        await ExecuteAsync(session, "INSERT INTO sales.child VALUES (1, 1)");
        await ExecuteAsync(session, "INSERT INTO external_child VALUES (1)");
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql)
        => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await ExecuteAsync(session, sql);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
