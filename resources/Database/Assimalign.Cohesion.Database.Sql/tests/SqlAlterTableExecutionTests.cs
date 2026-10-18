using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlAlterTableExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER DEFAULT: missing fields retain their frozen value after restart")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restart_ChangedDefault_ShouldPreserveMissingValues(bool hadDefault)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-default", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "ALTER TABLE t ADD extra INT " + (hadDefault ? "NOT NULL DEFAULT 7" : ""));
            await ExecuteAsync(session, "CREATE INDEX ix_extra ON t(extra)");

            // Act
            await ExecuteAsync(session, "ALTER TABLE t ALTER extra SET DEFAULT 9");
            await ExecuteAsync(session, "INSERT INTO t(id) VALUES (2)");

            // Assert
            (await RowsAsync(session, "SELECT extra FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { hadDefault ? 7 : null, 9 });
            (await RowsAsync(session, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'extra'"))[0][0].ShouldBe("9");
            await ExecuteAsync(session, "ALTER TABLE t ALTER extra DROP DEFAULT");
            if (hadDefault)
            {
                await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t(id) VALUES (3)"));
                (await RowsAsync(session, "SELECT extra FROM t WHERE extra = 7")).ShouldHaveSingleItem()[0].ShouldBe(7);
            }
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-default-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(reader, "SELECT extra FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { hadDefault ? 7 : null, 9 });
        (await RowsAsync(reader, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'extra'"))[0][0].ShouldBeNull();
        await ExecuteAsync(reader, "ALTER TABLE t ALTER extra SET DEFAULT 11");
        await ExecuteAsync(reader, "INSERT INTO t(id) VALUES (4)");
        (await RowsAsync(reader, "SELECT extra FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { hadDefault ? 7 : null, 9, 11 });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER DEFAULT: typed literals share CREATE and ADD validation")]
    [MemberData(nameof(SqlTypedLiteralTests.Literals), MemberType = typeof(SqlTypedLiteralTests))]
    [InlineData("INT", "-7", DatabaseType.Int32, -7)]
    [InlineData("INT", "+7", DatabaseType.Int32, 7)]
    [InlineData("DECIMAL(5,2)", "-12.34", DatabaseType.Decimal, "-12.34")]
    public async Task ExecuteAsync_TypedDefault_ShouldApplyOnlyToLaterWrites(string type, string literal, DatabaseType metadata, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-typed", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE t (id INT, v {type})");
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (1)");

        // Act
        await ExecuteAsync(session, $"ALTER TABLE t ALTER COLUMN v SET DEFAULT {literal}");
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (2)");

        // Assert
        if (metadata == DatabaseType.Decimal) { expected = -12.34m; }
        var rows = await RowsAsync(session, "SELECT v FROM t ORDER BY id");
        rows[0][0].ShouldBeNull();
        if (expected is byte[] bytes) { rows[1][0].ShouldBeOfType<byte[]>().ShouldBe(bytes); }
        else { rows[1][0].ShouldBe(expected); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER DEFAULT: failed conversion publishes no metadata")]
    [InlineData("2147483648")]
    [InlineData("'bad'")]
    [InlineData("DATE '2024-01-01'")]
    [InlineData("(1 / 0)")]
    public async Task ExecuteAsync_InvalidDefault_ShouldKeepDefinition(string value)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-invalid", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, v INT DEFAULT 7)");
        database.Catalog.TryGetTable("dbo", "t", out var before).ShouldBeTrue();
        ulong generation = database.Catalog.DdlGeneration;

        // Act / Assert
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"ALTER TABLE t ALTER v SET DEFAULT {value}"))).Message.ShouldContain("v", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out var after).ShouldBeTrue();
        after.ShouldBeSameAs(before);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (1)");
        (await RowsAsync(session, "SELECT v FROM t"))[0][0].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ALTER NOT NULL: stored and missing nulls validate atomically")]
    public async Task ExecuteAsync_Nullability_ShouldValidateStoredAndMissingFields()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-null", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, stored INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, NULL)");
        await ExecuteAsync(session, "ALTER TABLE t ADD missing INT");
        foreach (string name in new[] { "stored", "missing" })
        {
            database.Catalog.TryGetTable("dbo", "t", out var before).ShouldBeTrue();
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"ALTER TABLE t ALTER {name} SET NOT NULL"))).Message.ShouldContain(name, Case.Sensitive);
            database.Catalog.TryGetTable("dbo", "t", out var after).ShouldBeTrue();
            after.ShouldBeSameAs(before);
        }

        // Act
        await ExecuteAsync(session, "UPDATE t SET stored = 3, missing = 4");
        await ExecuteAsync(session, "ALTER TABLE t ALTER stored SET NOT NULL");
        await ExecuteAsync(session, "ALTER TABLE t ALTER missing SET NOT NULL");

        // Assert
        (await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'stored'"))[0][0].ShouldBe("NO");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE t SET stored = NULL"));
        await ExecuteAsync(session, "ALTER TABLE t ALTER stored DROP NOT NULL");
        await ExecuteAsync(session, "UPDATE t SET stored = NULL");
        (await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'stored'"))[0][0].ShouldBe("YES");
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ALTER id DROP NOT NULL"));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");
        error.Message.ShouldContain("Conflicting NULL/NOT NULL declarations for column 'id'.", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER identity: SET DEFAULT and DROP NOT NULL name the protected column")]
    [InlineData("SET DEFAULT 8")]
    [InlineData("DROP DEFAULT")]
    [InlineData("DROP NOT NULL")]
    public async Task ExecuteAsync_IdentityFacet_ShouldRefuseChange(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-identity", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY)");

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ALTER id " + action));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE025");
        error.Message.ShouldContain("id", Case.Sensitive);
        await ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(session, "SELECT id FROM t"))[0][0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME: rows indexes checks and both FK directions survive reopen")]
    public async Task Restart_RenamedObjects_ShouldRetainEveryDependent()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("rename", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedRenameAsync(session);

            // Act
            await ExecuteAsync(session, "ALTER TABLE p RENAME TO renamed");
            await ExecuteAsync(session, "ALTER TABLE renamed RENAME COLUMN id TO key_id");
            await ExecuteAsync(session, "ALTER TABLE renamed RENAME u TO unique_value");
            await ExecuteAsync(session, "ALTER TABLE c RENAME pid TO parent_id");

            // Assert
            await AssertRenamedAsync(session);
            database.Catalog.TryGetTable("dbo", "renamed", out var table).ShouldBeTrue();
            database.Catalog.GetIndexes(table.ObjectId).ShouldContain(index => index.Name == "ix_u" && index.ColumnNames.SequenceEqual(new[] { "unique_value" }));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT * FROM p"));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT id FROM renamed"));
        }
        await using var reopened = SqlDatabaseEngine.Create("rename-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await AssertRenamedAsync(reader);
        await ExecuteAsync(reader, "DELETE FROM renamed WHERE key_id = 1");
        (await RowsAsync(reader, "SELECT * FROM c")).ShouldBeEmpty();
        (await RowsAsync(reader, "SELECT * FROM renamed")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER: failed name changes and explicit transactions leave state intact")]
    [InlineData("RENAME TO other")]
    [InlineData("RENAME id TO v")]
    [InlineData("ALTER v SET DEFAULT 9")]
    [InlineData("ALTER v DROP DEFAULT")]
    [InlineData("ALTER v SET NOT NULL")]
    [InlineData("ALTER v DROP NOT NULL")]
    public async Task ExecuteAsync_ExplicitTransaction_ShouldSurviveRefusedDdl(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-tx", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, v INT DEFAULT 7)");
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (1)");

        // Act / Assert
        var error = await session.ExecuteAsync("ALTER TABLE t " + action, cancellationToken: CancellationToken.None);
        error.Status.ShouldBe(DatabaseResultStatus.Error);
        error.Diagnostics!.ShouldContain(diagnostic => diagnostic.Code == "COHSQLT003");
        (await RowsAsync(session, "SELECT v FROM t"))[0][0].ShouldBe(7);
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RENAME: collisions leave definition and generation unchanged")]
    [InlineData("ALTER TABLE t RENAME TO u", "COHSQLE013", "Table 'dbo.u' already exists.")]
    [InlineData("ALTER TABLE t RENAME TO t", "COHSQLE013", "Table 'dbo.t' already exists.")]
    [InlineData("ALTER TABLE t RENAME a TO a", "COHSQLE013", "Column 'a' already exists on 't'.")]
    [InlineData("ALTER TABLE t RENAME a TO b", "COHSQLE013", "Column 'b' already exists on 't'.")]
    public async Task ExecuteAsync_Collision_ShouldLeaveCatalogUnchanged(string sql, string code, string message)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-collision", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT, b INT)");
        await ExecuteAsync(session, "CREATE TABLE u(a INT)");
        ulong generation = database.Catalog.DdlGeneration;

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        error.Message.ShouldContain(message, Case.Sensitive);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 2)");
        (await RowsAsync(session, "SELECT * FROM t"))[0].ShouldBe(new object?[] { 1, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME: the executor refuses a target that is a system view")]
    public async Task ExecuteAsync_RenameIntoSystemView_ShouldLeaveCatalogUnchanged()
    {
        // Arrange: the bare catalog accepts namespace strings independently of SQL admission.
        // Exercise the executor guard directly, beyond the session's namespace protection.
        await using var engine = SqlDatabaseEngine.Create("rename-system-view", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var table = await database.Catalog.CreateTableAsync("INFORMATION_SCHEMA", "staging",
            [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), true)], [], CancellationToken.None);
        ulong generation = database.Catalog.DdlGeneration;
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            // Act
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var failure = await Should.ThrowAsync<DatabaseException>(() => executor.ExecuteAsync(
                new SqlRenamePlan(table, null, "TABLES"), new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None));

            // Assert
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE021");
            failure.Message.ShouldContain("System view 'INFORMATION_SCHEMA.TABLES' is read-only.", Case.Sensitive);
            database.Catalog.DdlGeneration.ShouldBe(generation);
            database.Catalog.TryGetTable("INFORMATION_SCHEMA", "staging", out var unchanged).ShouldBeTrue();
            unchanged.ShouldBeSameAs(table);
            database.Catalog.TryGetTable("INFORMATION_SCHEMA", "TABLES", out _).ShouldBeFalse();
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER: schema-owned tables retain their DDL protection")]
    [InlineData("RENAME TO u")]
    [InlineData("RENAME id TO key_id")]
    [InlineData("ALTER id SET DEFAULT 9")]
    [InlineData("ALTER id DROP NOT NULL")]
    public async Task ExecuteAsync_SchemaOwnedTable_ShouldRefuseAlter(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-owned", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
            [new CompiledSchemaTable("t", "Tests.Owned", [new CompiledSchemaColumn("id", DatabaseType.Int32, IsNullable: false)], null, [], [])], []);
        await database.ApplySchemaAsync(schema, cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(session, "ALTER TABLE t " + action));
        database.Catalog.TryGetTable("dbo", "t", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME: retained script claims refuse a rename naming the dependent")]
    public async Task ExecuteAsync_ScriptOwnedTarget_ShouldNameDependentWithoutChangingMetadata()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("rename-script");
        builder.Options.StorageStrategy = storage;
        builder.AddDatabase("db", database => database.Script("001-create", "CREATE TABLE t(id INT)")
            .Script("002-rename", "ALTER TABLE t RENAME TO u"));

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await builder.BuildAsync(CancellationToken.None));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLD002");
        error.Message.ShouldContain("001-create", Case.Sensitive);
        await using var reopened = SqlDatabaseEngine.Create("rename-script-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "t", out _).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "u", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SET DEFAULT: scalar calls preserve old missing values and bind after restart")]
    public async Task Restart_FunctionDefault_ShouldRetainCallsAndFrozenValue()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-call", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE FUNCTION default_value() RETURNS INT IMMUTABLE RETURN -9");
            await ExecuteAsync(session, "CREATE TABLE t(id INT)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "ALTER TABLE t ADD v INT DEFAULT 7");
            // Act
            await ExecuteAsync(session, "ALTER TABLE t ALTER v SET DEFAULT default_value()");
            await ExecuteAsync(session, "INSERT INTO t(id) VALUES (2)");
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-call-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(reader, "INSERT INTO t(id) VALUES (3)");
        // Assert
        (await RowsAsync(reader, "SELECT v FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 7, -9, -9 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME: quoted qualified identifiers retain bound CHECK meaning")]
    public async Task ExecuteAsync_QuotedNames_ShouldRewriteOnlyBoundIdentifierTokens()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-quoted", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SCHEMA \"my schema\"");
        await ExecuteAsync(session, "CREATE TABLE \"my schema\".\"my table\" (\"old name\" INT, label TEXT DEFAULT 'old name', CONSTRAINT ck CHECK (\"my table\".\"old name\" > 0 AND label <> 'old name'))");
        await ExecuteAsync(session, "INSERT INTO \"my schema\".\"my table\" VALUES (1, 'okay')");

        // Act
        await ExecuteAsync(session, "ALTER TABLE \"my schema\".\"my table\" RENAME \"old name\" TO \"new name\"");
        await ExecuteAsync(session, "ALTER TABLE \"my schema\".\"my table\" RENAME TO \"next table\"");

        // Assert
        (await RowsAsync(session, "SELECT \"new name\" FROM \"my schema\".\"next table\""))[0][0].ShouldBe(1);
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO \"my schema\".\"next table\" VALUES (-1, 'okay')"));
        database.Catalog.TryGetTable("my schema", "next table", out var table).ShouldBeTrue();
        table.Constraints.Single().CheckExpression!.ShouldContain("'old name'", Case.Sensitive);
        table.FindColumn("label")!.DefaultLiteral.ShouldBe("'old name'");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SET DEFAULT: incompatible function result refuses before publication")]
    public async Task ExecuteAsync_IncompatibleFunctionDefault_ShouldRetainDefaultAndGeneration()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-function-type", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE FUNCTION wrong_type() RETURNS DATE IMMUTABLE RETURN DATE '2024-01-01'");
        await ExecuteAsync(session, "CREATE TABLE t(id INT, v INT DEFAULT 7)");
        database.Catalog.TryGetTable("dbo", "t", out var before).ShouldBeTrue();
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ALTER v SET DEFAULT wrong_type()"));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE013");
        error.Message.ShouldContain("Column 'v': DEFAULT result type Date is incompatible with column type Int32.", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out var after).ShouldBeTrue();
        after.ShouldBeSameAs(before);
        after.FindColumn("v")!.DefaultLiteral.ShouldBe("7");
        database.Catalog.DdlGeneration.ShouldBe(generation);
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (1)");
        (await RowsAsync(session, "SELECT v FROM t"))[0][0].ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER domain DEFAULT: DROP inherits and explicit NULL survives reopen")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_DomainDefaultFacets_ShouldInheritAndPreserveNullOverride(bool notNull)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-domain-default", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE DOMAIN d AS INT DEFAULT 7 " + (notNull ? "NOT NULL" : ""));
            await ExecuteAsync(session, "CREATE TABLE t(id INT)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "ALTER TABLE t ADD v d DEFAULT 9");

            // Act
            await ExecuteAsync(session, "ALTER TABLE t ALTER v DROP DEFAULT");
            await ExecuteAsync(session, "INSERT INTO t(id) VALUES (2)");
            await ExecuteAsync(session, "ALTER TABLE t ALTER v SET DEFAULT NULL");

            // Assert
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            table.FindColumn("v")!.DefaultLiteral.ShouldBe("NULL");
            table.FindColumn("v")!.MissingValueLiteral.ShouldBe("9");
            (await RowsAsync(session, "SELECT v FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 9, 7 });
            if (notNull) { await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t(id) VALUES (3)")); }
            else { await ExecuteAsync(session, "INSERT INTO t(id) VALUES (3)"); }
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-domain-default-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        restored.Catalog.TryGetTable("dbo", "t", out var restoredTable).ShouldBeTrue();
        restoredTable.FindColumn("v")!.DefaultLiteral.ShouldBe("NULL");
        restoredTable.FindColumn("v")!.MissingValueLiteral.ShouldBe("9");
        if (notNull) { await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO t(id) VALUES (4)")); }
        else
        {
            await ExecuteAsync(reader, "INSERT INTO t(id) VALUES (4)");
            (await RowsAsync(reader, "SELECT v FROM t WHERE id = 4"))[0][0].ShouldBeNull();
        }
        await ExecuteAsync(reader, "ALTER TABLE t ALTER v DROP DEFAULT");
        await ExecuteAsync(reader, "INSERT INTO t(id) VALUES (5)");
        (await RowsAsync(reader, "SELECT v FROM t WHERE id = 5"))[0][0].ShouldBe(7);
        (await RowsAsync(reader, "SELECT v FROM t WHERE id = 1"))[0][0].ShouldBe(9);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME: persisted deep CHECKs use the ceiling WHERE carrier")]
    public async Task Restart_DeepCheckRenames_ShouldPreserveAndEnforceDefinition()
    {
        // Arrange
        using var stack = new SqlDepthTestThread(16 * 1024);
        void ExecuteOnStack(SqlDatabaseSession session, string sql)
            => stack.Run(() => { ExecuteAsync(session, sql).GetAwaiter().GetResult(); return true; }).Failure.ShouldBeNull();
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("rename-deep", new SqlDatabaseEngineOptions { StorageStrategy = storage, ExpressionNestingLimit = 1024 }))
        {
            var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            string predicate = string.Concat(Enumerable.Repeat("t.a + (", 300)) + "t.a" + new string(')', 300) + " > 0";
            ExecuteOnStack(session, $"CREATE TABLE t(a INT, b INT, CONSTRAINT ck CHECK ({predicate}))");
            ExecuteOnStack(session, "INSERT INTO t VALUES (1, 2)");

            // Act
            ExecuteOnStack(session, "ALTER TABLE t RENAME TO u");
            ExecuteOnStack(session, "ALTER TABLE u RENAME a TO c");
        }
        // Assert: an engine using the default admission limit also rewrites the persisted CHECK.
        await using var reopened = SqlDatabaseEngine.Create("rename-deep-reopen", new SqlDatabaseEngineOptions { StorageStrategy = storage.CaptureDurableImages() });
        var (restored, failure) = stack.Run(() => reopened.OpenDatabaseAsync("db", CancellationToken.None).GetAwaiter().GetResult());
        failure.ShouldBeNull();
        await using var reader = await restored!.CreateSessionAsync(CancellationToken.None);
        ExecuteOnStack(reader, "ALTER TABLE u RENAME b TO d");
        ExecuteOnStack(reader, "ALTER TABLE u RENAME TO v");
        (await RowsAsync(reader, "SELECT c, d FROM v"))[0].ShouldBe(new object?[] { 1, 2 });
        stack.Run(() => { ExecuteAsync(reader, "INSERT INTO v VALUES (-1, 2)").GetAwaiter().GetResult(); return true; }).Failure.ShouldBeOfType<SqlConstraintViolationException>();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RENAME: all republished children bind before their next write")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RenameParent_ShouldBindEveryPublishedChild(bool column)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-child-bind", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE c1(id INT REFERENCES p(id), v INT DEFAULT ABS(-1), CHECK (v > 0))");
        await ExecuteAsync(session, "CREATE TABLE c2(id INT REFERENCES p(id), v INT DEFAULT ABS(-2), CHECK (v > 0))");
        await ExecuteAsync(session, "INSERT INTO p VALUES (1)");
        long before = database.Definitions.BindCount;

        // Act
        await ExecuteAsync(session, column ? "ALTER TABLE p RENAME id TO k" : "ALTER TABLE p RENAME TO q");
        long after = database.Definitions.BindCount;
        await ExecuteAsync(session, "INSERT INTO c1(id) VALUES (1)");
        await ExecuteAsync(session, "INSERT INTO c2(id) VALUES (1)");

        // Assert
        after.ShouldBeGreaterThanOrEqualTo(before + 3);
        database.Definitions.BindCount.ShouldBe(after);
    }

    internal static async Task SeedRenameAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE FUNCTION id_value(id INT) RETURNS INT IMMUTABLE RETURN id");
        await ExecuteAsync(session, "CREATE TABLE p (id INT PRIMARY KEY, u INT UNIQUE, parent INT REFERENCES p(id) ON DELETE CASCADE, label TEXT DEFAULT 'id', CONSTRAINT ck CHECK (id > 0 AND label <> 'id'))");
        await ExecuteAsync(session, "CREATE INDEX ix_u ON p(u)");
        await ExecuteAsync(session, "CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)");
        await ExecuteAsync(session, "CREATE TABLE r (pid INT REFERENCES p(id) ON DELETE RESTRICT)");
        await ExecuteAsync(session, "INSERT INTO p VALUES (1, 10, NULL, 'ok'), (2, 20, 1, 'ok')");
        await ExecuteAsync(session, "INSERT INTO c VALUES (3, 1)");
    }

    private static async Task AssertRenamedAsync(SqlDatabaseSession session)
    {
        (await RowsAsync(session, "SELECT key_id FROM renamed WHERE unique_value = 10")).ShouldHaveSingleItem()[0].ShouldBe(1);
        session.LastStatementMetrics!.AccessPath.ShouldStartWith("seek:");
        (await RowsAsync(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'renamed'"))[0][0].ShouldBe("renamed");
        (await RowsAsync(session, "SELECT COLUMN_NAME FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'renamed' AND INDEX_NAME = 'ix_u'"))[0][0].ShouldBe("unique_value");
        (await RowsAsync(session, "SELECT parent_id FROM c")).ShouldHaveSingleItem()[0].ShouldBe(1);
        var check = (await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_NAME = 'ck'"))[0][0]!.ToString()!;
        check.ShouldContain("key_id", Case.Sensitive);
        check.ShouldContain("'id'", Case.Sensitive);
        (await RowsAsync(session, "SELECT id_value(5) FROM renamed WHERE key_id = 1"))[0][0].ShouldBe(5);
        await ExecuteAsync(session, "INSERT INTO r VALUES (1)");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DELETE FROM renamed WHERE key_id = 1"));
        await ExecuteAsync(session, "DELETE FROM r");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO renamed VALUES (1, 30, NULL, 'ok')"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO renamed VALUES (3, 10, NULL, 'ok')"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO renamed VALUES (-1, 30, NULL, 'ok')"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO renamed VALUES (3, 30, 999, 'ok')"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (4, 999)"));
    }

    internal static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    internal static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }

}
