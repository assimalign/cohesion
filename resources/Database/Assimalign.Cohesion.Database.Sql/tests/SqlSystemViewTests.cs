using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises the virtual catalog relations through ordinary SQL sessions.</summary>
public sealed class SqlSystemViewTests
{
    /// <summary>Ad-hoc and compiled objects share the ISO surface and retain their ownership separately.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Ad-hoc and compiled objects retain distinct ownership")]
    public async Task TablesAndOwnership_ShouldIncludeAdhocAndCompiledObjects()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", EngineModel.Sql, false,
            [], [new CompiledSchemaTable("managed", "Tests.Managed",
                [new("id", DatabaseType.Int32, false), new("label", DatabaseType.String, true)],
                new CompiledSchemaKey("pk_managed", ["id"]),
                [new CompiledSchemaIndex("ix_managed_label", ["label"])], [])], [], [], [], []);
        await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema);
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE adhoc (id INT PRIMARY KEY, label VARCHAR(20))");
        await session.ExecuteAsync("CREATE INDEX ix_adhoc_label ON adhoc(label)");

        // Act / Assert
        var tables = await RowsAsync(session,
            "SELECT TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME");
        tables.Count.ShouldBe(2);
        tables[0].ShouldBe(new object?[] { "app", "dbo", "adhoc", "BASE TABLE" });
        tables[1].ShouldBe(new object?[] { "app", "dbo", "managed", "BASE TABLE" });

        var ownership = await RowsAsync(session,
            "SELECT TABLE_NAME, OBJECT_NAME, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE OBJECT_TYPE = 'TABLE' ORDER BY TABLE_NAME");
        ownership.Count.ShouldBe(2);
        ownership[0].ShouldBe(new object?[] { "adhoc", "adhoc", "Adhoc", null });
        ownership[1].ShouldBe(new object?[] { "managed", "managed", "Schema", "app" });
        var indexes = await RowsAsync(session,
            "SELECT OBJECT_NAME, OWNER, OWNING_SCHEMA FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE OBJECT_TYPE = 'INDEX' AND OBJECT_NAME LIKE 'ix_%' ORDER BY OBJECT_NAME");
        indexes.Count.ShouldBe(2);
        indexes[0].ShouldBe(new object?[] { "ix_adhoc_label", "Adhoc", null });
        indexes[1].ShouldBe(new object?[] { "ix_managed_label", "Schema", "app" });

        await using var iso = (await session.ExecuteAsync("SELECT * FROM INFORMATION_SCHEMA.TABLES"))
            .ShouldBeAssignableTo<QueryResultSet>();
        iso.Columns.Select(column => column.Name).ShouldNotContain("OWNER");
        iso.Columns.Select(column => column.Name).ShouldNotContain("OWNING_SCHEMA");
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("INFORMATION_SCHEMA", "TABLES", out _).ShouldBeFalse();
        catalog.TryGetTable("COHESION_SCHEMA", "OBJECT_OWNERSHIP", out _).ShouldBeFalse();
    }

    /// <summary>Columns expose ordinal positions, normalized SQL types, nullability, and default literals.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Columns expose SQL types, ordinals, nullability, and defaults")]
    public async Task Columns_ShouldExposeDeclaredMetadataAndIsoResultTypes()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE orders (id INT PRIMARY KEY, label VARCHAR(40) DEFAULT 'new', amount DECIMAL(12, 3) NOT NULL DEFAULT 0)");

        // Act / Assert
        const string query = "SELECT COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION";
        var rows = await RowsAsync(session, query);
        rows.Count.ShouldBe(3);
        rows.Select(row => row[0]).ShouldBe(new object?[] { "id", "label", "amount" });
        rows.Select(row => Number(row[1])).ShouldBe(new long[] { 1, 2, 3 });
        rows.Select(row => row[2]).ShouldBe(new object?[] { "INTEGER", "CHARACTER VARYING", "NUMERIC" });
        rows.Select(row => row[3]).ShouldBe(new object?[] { "NO", "YES", "NO" });
        rows.Select(row => row[4]).ShouldBe(new object?[] { null, "'new'", "0" });
        var sizes = await RowsAsync(session,
            "SELECT COLUMN_NAME, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION");
        sizes[0].ShouldBe(new object?[] { "id", null, 32L, 2L, 0L });
        sizes[1].ShouldBe(new object?[] { "label", 40L, null, null, null });
        sizes[2].ShouldBe(new object?[] { "amount", null, 12L, 10L, 3L });

        await using var result = (await session.ExecuteAsync(query)).ShouldBeAssignableTo<QueryResultSet>();
        result.Columns.Select(column => column.Name).ShouldBe(new[]
            { "COLUMN_NAME", "ORDINAL_POSITION", "DATA_TYPE", "IS_NULLABLE", "COLUMN_DEFAULT" });
        result.Columns[0].Type.ShouldBe(DatabaseType.String);
        result.Columns[1].Type.ShouldBe(DatabaseType.Int64);
        result.Columns.Skip(2).Select(column => column.Type).ShouldAllBe(type => type == DatabaseType.String);
    }

    /// <summary>Virtual relations use the ordinary predicate, expression, ordering, and pagination semantics.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: SELECT shares ordinary filtering, projection, ordering, and pagination")]
    public async Task Select_ShouldSupportParametersProjectionOrderingDistinctCountAndPagination()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE orders (id INT, note TEXT, quantity INT)");
        await session.ExecuteAsync("CREATE TABLE customers (id INT)");

        // Act / Assert
        var rows = await RowsAsync(session,
            "SELECT COLUMN_NAME AS field, ORDINAL_POSITION + 10 AS position FROM information_schema.columns WHERE TABLE_NAME = @table AND ORDINAL_POSITION > @minimum ORDER BY ORDINAL_POSITION DESC LIMIT 1 OFFSET 1",
            new Dictionary<string, object?> { ["table"] = "orders", ["minimum"] = 1 });
        rows.Count.ShouldBe(1);
        rows[0][0].ShouldBe("note");
        Number(rows[0][1]).ShouldBe(12);
        (await RowsAsync(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'orders'"))
            .Single().ShouldBe(new object?[] { "orders" });
        (await RowsAsync(session, "SELECT DISTINCT TABLE_SCHEMA FROM INFORMATION_SCHEMA.COLUMNS"))
            .Single().ShouldBe(new object?[] { "dbo" });
        Number((await RowsAsync(session, "SELECT COUNT(*) AS total FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders'"))[0][0])
            .ShouldBe(3);
        (await RowsAsync(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = NULL"))
            .ShouldBeEmpty();
    }

    /// <summary>Primary, unique, reference, and check constraints preserve their names and column order.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Constraint names, composite ordinals, references, and checks are queryable")]
    public async Task Constraints_ShouldExposeCompositeKeysReferenceTargetsAndCheckClauses()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await CreateConstrainedTablesAsync(session);

        // Act / Assert
        var constraints = await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, TABLE_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS ORDER BY CONSTRAINT_NAME");
        constraints.Count.ShouldBe(5);
        constraints[0].ShouldBe(new object?[] { "ck_quantity", "child", "CHECK" });
        constraints[1].ShouldBe(new object?[] { "fk_parent", "child", "FOREIGN KEY" });
        constraints[2].ShouldBe(new object?[] { "pk_child", "child", "PRIMARY KEY" });
        constraints[3].ShouldBe(new object?[] { "pk_parent", "parent", "PRIMARY KEY" });
        constraints[4].ShouldBe(new object?[] { "uq_child", "child", "UNIQUE" });

        var keys = await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_NAME = 'child' ORDER BY CONSTRAINT_NAME, ORDINAL_POSITION");
        keys.Select(row => row[0]).ShouldBe(new object?[] { "fk_parent", "fk_parent", "pk_child", "uq_child", "uq_child" });
        keys.Select(row => row[1]).ShouldBe(new object?[] { "parent_b", "parent_a", "id", "parent_a", "id" });
        keys.Select(row => Number(row[2])).ShouldBe(new long[] { 1, 2, 1, 1, 2 });

        var references = await RowsAsync(session,
            "SELECT CONSTRAINT_CATALOG, CONSTRAINT_SCHEMA, CONSTRAINT_NAME, UNIQUE_CONSTRAINT_CATALOG, UNIQUE_CONSTRAINT_SCHEMA, UNIQUE_CONSTRAINT_NAME, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS");
        references.Single().ShouldBe(new object?[] { "app", "dbo", "fk_parent", "app", "dbo", "pk_parent", "CASCADE" });
        var checks = await RowsAsync(session,
            "SELECT CONSTRAINT_CATALOG, CONSTRAINT_SCHEMA, CONSTRAINT_NAME, CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS");
        checks.Count.ShouldBe(1);
        checks[0].Take(3).ShouldBe(new object?[] { "app", "dbo", "ck_quantity" });
        checks[0][3].ShouldBeOfType<string>().ShouldContain("quantity > 0");
    }

    /// <summary>Foreign keys identify the matching unique constraint and their configured delete behavior.</summary>
    /// <param name="action">The supported referential action.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - System views: References identify unique constraints and delete rules")]
    [InlineData("RESTRICT")]
    [InlineData("CASCADE")]
    public async Task References_ShouldExposeUniqueConstraintAndDeleteAction(string action)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE parent (id INT PRIMARY KEY, code TEXT, CONSTRAINT uq_code UNIQUE(code))");
        await session.ExecuteAsync($"CREATE TABLE child (code TEXT, CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES parent(code) ON DELETE {action})");

        // Act / Assert
        (await RowsAsync(session,
            "SELECT CONSTRAINT_NAME, UNIQUE_CONSTRAINT_NAME, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS"))
            .Single().ShouldBe(new object?[] { "fk_code", "uq_code", action });
    }

    /// <summary>The Cohesion index view includes each index key and its unique and primary-key flags.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Indexes expose key order and unique and primary flags")]
    public async Task Indexes_ShouldExposeKeyOrderAndIndexKinds()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE orders (id INT, a INT, b INT, CONSTRAINT pk_orders PRIMARY KEY(id), CONSTRAINT uq_pair UNIQUE(b,a))");
        await session.ExecuteAsync("CREATE INDEX ix_pair ON orders(a,b)");

        // Act / Assert
        var rows = await RowsAsync(session,
            "SELECT INDEX_NAME, COLUMN_NAME, ORDINAL_POSITION, IS_UNIQUE, IS_PRIMARY_KEY FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'orders' ORDER BY INDEX_NAME, ORDINAL_POSITION");
        rows.Select(row => row[0]).ShouldBe(new object?[] { "ix_pair", "ix_pair", "pk_orders", "uq_pair", "uq_pair" });
        rows.Select(row => row[1]).ShouldBe(new object?[] { "a", "b", "id", "b", "a" });
        rows.Select(row => Number(row[2])).ShouldBe(new long[] { 1, 2, 1, 1, 2 });
        rows.Select(row => row[3]).ShouldBe(new object?[] { "NO", "NO", "YES", "YES", "YES" });
        rows.Select(row => row[4]).ShouldBe(new object?[] { "NO", "NO", "YES", "NO", "NO" });
    }

    /// <summary>Catalog changes are visible on the next query and table drops remove every dependent metadata row.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: DROP TABLE removes every dependent metadata row")]
    public async Task DropTable_ShouldRemoveRowsFromEverySystemView()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await CreateConstrainedTablesAsync(session);
        await session.ExecuteAsync("CREATE INDEX ix_quantity ON child(quantity)");
        await session.ExecuteAsync("ALTER TABLE child ADD COLUMN note TEXT DEFAULT 'added'");
        (await RowsAsync(session,
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'child' AND COLUMN_NAME = 'note'"))
            .Single().ShouldBe(new object?[] { "note" });
        // Act
        await session.ExecuteAsync("DROP TABLE child", cancellationToken: CancellationToken.None);

        // Assert
        foreach (string view in new[] { "INFORMATION_SCHEMA.TABLES", "INFORMATION_SCHEMA.COLUMNS", "INFORMATION_SCHEMA.TABLE_CONSTRAINTS",
            "INFORMATION_SCHEMA.KEY_COLUMN_USAGE", "COHESION_SCHEMA.INDEXES", "COHESION_SCHEMA.OBJECT_OWNERSHIP" })
        {
            (await RowsAsync(session, $"SELECT * FROM {view} WHERE TABLE_NAME = 'child'")).ShouldBeEmpty();
        }
        (await RowsAsync(session, "SELECT * FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT * FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS")).ShouldBeEmpty();
        await session.ExecuteAsync("DROP TABLE parent");
        (await RowsAsync(session, "SELECT * FROM INFORMATION_SCHEMA.TABLES")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.OBJECT_OWNERSHIP")).ShouldBeEmpty();
    }

    /// <summary>All system-view names reject DML, DDL, and collision attempts with the same stable diagnostic.</summary>
    /// <param name="view">The reserved qualified system-view name.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - System views: DML, DDL, and name collisions share the read-only diagnostic")]
    [InlineData("INFORMATION_SCHEMA.TABLES")]
    [InlineData("INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("INFORMATION_SCHEMA.TABLE_CONSTRAINTS")]
    [InlineData("INFORMATION_SCHEMA.KEY_COLUMN_USAGE")]
    [InlineData("INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")]
    [InlineData("INFORMATION_SCHEMA.CHECK_CONSTRAINTS")]
    [InlineData("COHESION_SCHEMA.INDEXES")]
    [InlineData("COHESION_SCHEMA.OBJECT_OWNERSHIP")]
    public async Task Mutations_ShouldRefuseEverySystemView(string view)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        // Act / Assert
        foreach (string sql in new[]
        {
            $"INSERT INTO {view} VALUES ('forbidden')",
            $"UPDATE {view} SET TABLE_NAME = 'forbidden'",
            $"DELETE FROM {view}",
            $"DROP TABLE {view}",
            $"DROP TABLE IF EXISTS {view}",
            $"ALTER TABLE {view} ADD COLUMN extra INT",
            $"ALTER TABLE {view} DROP COLUMN extra",
            $"ALTER TABLE {view} ADD CONSTRAINT ck_extra CHECK(extra > 0)",
            $"ALTER TABLE {view} DROP CONSTRAINT ck_extra",
            $"CREATE INDEX forbidden ON {view}(TABLE_NAME)",
            $"DROP INDEX forbidden ON {view}",
            $"CREATE TABLE {view} (id INT)",
            $"CREATE TABLE IF NOT EXISTS {view} (id INT)",
        })
        {
            var exception = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
            exception.Message.ShouldBe($"System view '{view}' is read-only.");
        }

        (await RowsAsync(session, $"SELECT * FROM {view}")).ShouldBeEmpty();
        var collision = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"CREATE TABLE {view.ToLowerInvariant()} (id INT)"));
        collision.Message.ShouldBe($"System view '{view}' is read-only.");

        await session.ExecuteAsync("BEGIN");
        var transactionDdl = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"DROP TABLE {view}"));
        transactionDdl.Message.ShouldBe($"System view '{view}' is read-only.");
        await session.ExecuteAsync("ROLLBACK");
    }

    /// <summary>Metadata follows the session's database binding even when another database owns identical names.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Metadata remains scoped to the session database")]
    public async Task SystemViews_ShouldRemainScopedToTheCurrentDatabase()
    {
        // Arrange
        await using var engine = CreateEngine();
        var first = await engine.CreateDatabaseAsync("first");
        var second = await engine.CreateDatabaseAsync("second");
        await using var session = await first.CreateSessionAsync();
        await using var other = await second.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE marker (local_value INT, CONSTRAINT pk_first PRIMARY KEY(local_value))");
        await other.ExecuteAsync("CREATE TABLE marker (remote_value TEXT, CONSTRAINT uq_second UNIQUE(remote_value))");
        await other.ExecuteAsync("CREATE TABLE remote_only (id INT)");

        // Act / Assert
        (await RowsAsync(session, "SELECT TABLE_CATALOG, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES"))
            .Single().ShouldBe(new object?[] { "first", "marker" });
        (await RowsAsync(session, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS"))
            .Single().ShouldBe(new object?[] { "local_value" });
        (await RowsAsync(session, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS"))
            .Single().ShouldBe(new object?[] { "pk_first" });
        (await RowsAsync(session, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE"))
            .Single().ShouldBe(new object?[] { "pk_first" });
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES"))
            .Single().ShouldBe(new object?[] { "pk_first" });
        (await RowsAsync(session, "SELECT DISTINCT TABLE_CATALOG FROM COHESION_SCHEMA.OBJECT_OWNERSHIP"))
            .Single().ShouldBe(new object?[] { "first" });
        (await RowsAsync(other, "SELECT TABLE_CATALOG, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME"))
            .Select(row => row[1]).ShouldBe(new object?[] { "marker", "remote_only" });
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT TABLE_NAME FROM second.INFORMATION_SCHEMA.TABLES"));
        session.Database.ShouldBeSameAs(first);
        other.Database.ShouldBeSameAs(second);
    }

    /// <summary>Unknown columns fail binding even when a system view currently has no rows.</summary>
    /// <param name="query">A SELECT that refers to an unknown metadata column.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - System views: Unknown columns fail binding even on empty views")]
    [InlineData("SELECT missing FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE missing = 1")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY missing")]
    public async Task Select_ShouldRejectUnknownColumnsOnEmptyViews(string query)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
    }

    /// <summary>A returned result retains the statement's catalog image while later DDL changes the live catalog.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Results retain their catalog image during deferred enumeration")]
    public async Task ResultEnumeration_ShouldRetainTheStatementCatalogImage()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await writer.ExecuteAsync("CREATE TABLE orders (id INT, label TEXT)");
        // Act
        await using var result = (await reader.ExecuteAsync(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION"))
            .ShouldBeAssignableTo<QueryResultSet>();
        await writer.ExecuteAsync("DROP TABLE orders");
        await writer.ExecuteAsync("CREATE TABLE orders (replacement INT)");

        // Assert
        (await ReadRowsAsync(result)).Select(row => row[0]).ShouldBe(new object?[] { "id", "label" });
        (await RowsAsync(reader, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders'"))
            .Single().ShouldBe(new object?[] { "replacement" });
    }

    /// <summary>Snapshot isolation fixes catalog visibility at transaction begin, including before its first query.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: Snapshot transactions retain BEGIN-time metadata across DDL")]
    public async Task SnapshotTransaction_ShouldRetainBeginTimeCatalogAcrossDdl()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await CreateConstrainedTablesAsync(writer);
        // Act
        var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // The first metadata read is deliberately after every DDL commit: the
        // capture belongs to BEGIN, not to the first access of each view.
        await writer.ExecuteAsync("DROP TABLE child");
        await writer.ExecuteAsync("ALTER TABLE parent ADD COLUMN new_column TEXT");
        await writer.ExecuteAsync("CREATE TABLE newcomer (id INT)");

        // Assert
        (await RowsAsync(reader, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME"))
            .Select(row => row[0]).ShouldBe(new object?[] { "child", "parent" });
        (await RowsAsync(reader, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'parent' ORDER BY ORDINAL_POSITION"))
            .Select(row => row[0]).ShouldBe(new object?[] { "a", "b" });
        (await RowsAsync(reader, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_NAME = 'child' ORDER BY CONSTRAINT_NAME"))
            .Select(row => row[0]).ShouldBe(new object?[] { "ck_quantity", "fk_parent", "pk_child", "uq_child" });
        (await RowsAsync(reader, "SELECT UNIQUE_CONSTRAINT_NAME, DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS"))
            .Single().ShouldBe(new object?[] { "pk_parent", "CASCADE" });
        (await RowsAsync(reader, "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS"))
            .Single().ShouldBe(new object?[] { "ck_quantity" });
        (await RowsAsync(reader, "SELECT DISTINCT INDEX_NAME FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'child' ORDER BY INDEX_NAME"))
            .Select(row => row[0]).ShouldBe(new object?[] { "pk_child", "uq_child" });
        await transaction.RollbackAsync();

        (await RowsAsync(reader, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME"))
            .Select(row => row[0]).ShouldBe(new object?[] { "newcomer", "parent" });
        (await RowsAsync(reader, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'parent' ORDER BY ORDINAL_POSITION"))
            .Select(row => row[0]).ShouldBe(new object?[] { "a", "b", "new_column" });
        (await RowsAsync(reader, "SELECT * FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")).ShouldBeEmpty();
    }

    /// <summary>ReadCommitted refreshes its catalog image for every statement in the same transaction.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - System views: ReadCommitted refreshes metadata for every statement")]
    public async Task ReadCommittedTransaction_ShouldRefreshCatalogPerStatement()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("app");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await writer.ExecuteAsync("CREATE TABLE orders (id INT)");
        // Act / Assert
        var transaction = await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        Number((await RowsAsync(reader, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS"))[0][0]).ShouldBe(1);
        await writer.ExecuteAsync("ALTER TABLE orders ADD COLUMN label TEXT");
        Number((await RowsAsync(reader, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS"))[0][0]).ShouldBe(2);
        await writer.ExecuteAsync("DROP TABLE orders");
        (await RowsAsync(reader, "SELECT * FROM INFORMATION_SCHEMA.TABLES")).ShouldBeEmpty();
        await transaction.CommitAsync();
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "system-views" });

    private static async Task CreateConstrainedTablesAsync(IDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE parent (a INT, b INT, CONSTRAINT pk_parent PRIMARY KEY(b,a))");
        await session.ExecuteAsync("CREATE TABLE child (id INT, parent_a INT, parent_b INT, quantity INT, CONSTRAINT pk_child PRIMARY KEY(id), CONSTRAINT uq_child UNIQUE(parent_a,id), CONSTRAINT fk_parent FOREIGN KEY(parent_b,parent_a) REFERENCES parent(b,a) ON DELETE CASCADE, CONSTRAINT ck_quantity CHECK(quantity > 0))");
    }

    private static long Number(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        return await ReadRowsAsync(result);
    }

    private static async Task<List<object?[]>> ReadRowsAsync(QueryResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
