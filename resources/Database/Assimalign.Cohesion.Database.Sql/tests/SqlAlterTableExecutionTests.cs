using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
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
    [InlineData("(1 + 2)")]
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
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "ALTER TABLE t ALTER id DROP NOT NULL"));
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
    [InlineData("ALTER TABLE t RENAME TO u")]
    [InlineData("ALTER TABLE t RENAME TO t")]
    [InlineData("ALTER TABLE t RENAME a TO a")]
    [InlineData("ALTER TABLE t RENAME a TO b")]
    [InlineData("ALTER TABLE INFORMATION_SCHEMA.t RENAME TO TABLES")]
    public async Task ExecuteAsync_Collision_ShouldLeaveCatalogUnchanged(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-collision", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT, b INT)");
        await ExecuteAsync(session, "CREATE TABLE u(a INT)");
        await ExecuteAsync(session, "CREATE TABLE INFORMATION_SCHEMA.t(a INT)");
        ulong generation = database.Catalog.DdlGeneration;

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
        database.Catalog.DdlGeneration.ShouldBe(generation);
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 2)");
        (await RowsAsync(session, "SELECT * FROM t"))[0].ShouldBe(new object?[] { 1, 2 });
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
