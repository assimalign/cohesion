using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: persisted declarations refuse skipped invalid literals before publication (#1788)")]
    [InlineData("CREATE TABLE defaulted (n INT DEFAULT (COALESCE(1, 'x')))", "CS02022", "22P02")]
    [InlineData("CREATE TABLE checked (n INT, CHECK (COALESCE(n, 'x') > 0))", "CS02022", "22P02")]
    [InlineData("CREATE TABLE checked (n INT, CHECK (CASE WHEN TRUE THEN n ELSE 'x' END > 0))", "CS02022", "22P02")]
    [InlineData("CREATE DOMAIN bad AS INT CHECK (COALESCE(VALUE, 'x') > 0)", "CS02022", "22P02")]
    [InlineData("CREATE VIEW bad AS SELECT COALESCE(1, 'x') AS n", "CS02022", "22P02")]
    [InlineData("CREATE INDEX bad ON t(n) WHERE COALESCE(n, 'x') > 0", "CS02022", "22P02")]
    [InlineData("CREATE FUNCTION bad() RETURNS INT IMMUTABLE RETURN COALESCE(1, 'x')", "CS02022", "22P02")]
    [InlineData("CREATE TABLE bad (n INT DEFAULT (COALESCE(1, CAST('x' AS TEXT))))", "CS02080", "42804")]
    [InlineData("CREATE TABLE bad (n INT, CHECK (COALESCE(n, CAST('x' AS TEXT)) > 0))", "CS02080", "42804")]
    [InlineData("CREATE DOMAIN bad AS INT CHECK (COALESCE(VALUE, CAST('x' AS TEXT)) > 0)", "CS02080", "42804")]
    [InlineData("CREATE VIEW bad AS SELECT COALESCE(1, CAST('x' AS TEXT)) AS n", "CS02080", "42804")]
    [InlineData("CREATE INDEX bad ON t(n) WHERE COALESCE(n, CAST('x' AS TEXT)) > 0", "CS02080", "42804")]
    public async Task Declare_InvalidAlternatives_ShouldRefuseBeforePersisting(string sql, string code, string postgresState)
    {
        // Arrange: PostgreSQL 17.11 observed the SQLSTATE recorded beside each statement.
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(n INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Execute(session, sql));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        _output.WriteLine($"PostgreSQL 17 SQLSTATE {postgresState}; engine {code}: {sql}");
        database.Catalog.Tables.Count().ShouldBe(1);
        database.Catalog.Views.ShouldBeEmpty();
        database.Catalog.Domains.ShouldBeEmpty();
        database.Catalog.Functions.ShouldBeEmpty();
        database.Catalog.GetIndexes(database.Catalog.Tables.Single().ObjectId).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: all accepted persisted kinds retain values, metadata and refusals across file reopen (#1788)")]
    public async Task Open_AllPersistedKinds_ShouldRetainBinding()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-common-types", Guid.NewGuid().ToString("N"));
        string[]? metadata = null;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                await using var engine = SqlDatabaseEngine.Create("common-persisted", new() { RootPath = root });
                var database = pass == 0 ? await engine.CreateDatabaseAsync("db", CancellationToken.None)
                    : await engine.OpenDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                if (pass == 0)
                {
                    await Execute(session, "CREATE DOMAIN positive AS DECIMAL DEFAULT 2 CHECK (COALESCE(VALUE, '1') > 0)");
                    await Execute(session, "CREATE FUNCTION choice() RETURNS DECIMAL IMMUTABLE RETURN CASE WHEN FALSE THEN 1 ELSE 2.5 END");
                    await Execute(session, "CREATE TABLE t(id INT, n positive, amount DECIMAL DEFAULT (COALESCE(NULL::INT, 2.5)), f DECIMAL DEFAULT choice(), CHECK (CASE WHEN n IS NULL THEN 1 ELSE n END > 0))");
                    await Execute(session, "CREATE INDEX ix ON t(id) WHERE COALESCE(n, '1') > 0");
                    await Execute(session, "CREATE VIEW v AS SELECT id, CASE WHEN id = 1 THEN 1 ELSE amount END AS c, COALESCE(n, '3') AS n, f FROM t");
                }

                // Act: write and exercise every definition on both sides of a real file reopen.
                await Execute(session, $"INSERT INTO t(id) VALUES ({pass + 1})");
                var failure = await Should.ThrowAsync<DatabaseException>(() => Execute(session, "INSERT INTO t(id,n) VALUES (9, -1)"));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03038");
                var badDefault = await Should.ThrowAsync<DatabaseException>(() => Execute(session, "ALTER TABLE t ALTER amount SET DEFAULT COALESCE(1, 'x')"));
                badDefault.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
                var badCheck = await Should.ThrowAsync<DatabaseException>(() => Execute(session, "ALTER TABLE t ADD CONSTRAINT bad CHECK(COALESCE(id, 'x') > 0)"));
                badCheck.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
                foreach (string refused in new[] {
                    "CREATE TABLE bad(n INT DEFAULT COALESCE(1, 'x'))",
                    "CREATE DOMAIN bad AS INT CHECK(COALESCE(VALUE, 'x') > 0)",
                    "CREATE VIEW bad AS SELECT COALESCE(1, 'x') AS n",
                    "CREATE INDEX bad ON t(id) WHERE COALESCE(id, 'x') > 0",
                    "CREATE FUNCTION bad() RETURNS INT IMMUTABLE RETURN COALESCE(1, 'x')" })
                {
                    (await Should.ThrowAsync<DatabaseException>(() => Execute(session, refused))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
                }
                await using var result = (await session.ExecuteAsync("SELECT * FROM v ORDER BY id", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

                // Assert
                result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Int32, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal });
                result.Columns.Select(column => column.Name).ShouldBe(new[] { "id", "c", "n", "f" });
                var signature = result.Columns.Select(column => $"{column.Ordinal}:{column.Name}:{column.Type}:{column.MaxLength}:{column.IsNullable}").ToArray();
                if (metadata is null) { metadata = signature; }
                else { signature.ShouldBe(metadata); }
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    count++;
                    row.GetValue(0).ShouldBe(count);
                    row.GetValue(1).ShouldBe(count == 1 ? 1m : 2.5m);
                    row.GetValue(2).ShouldBe(2m);
                    row.GetValue(3).ShouldBe(2.5m);
                }
                count.ShouldBe(pass + 1);
                (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT id FROM t WHERE COALESCE(n, '1') > 0 ORDER BY id")).Count.ShouldBe(pass + 1);
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: unsupported persisted forms retain their existing boundaries (#1788)")]
    [InlineData("CREATE DOMAIN d AS INT DEFAULT COALESCE(1, 'x')", "CS03301")]
    [InlineData("ALTER DOMAIN d SET DEFAULT COALESCE(1, 'x')", "CS00003")]
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS INT RETURN COALESCE(NULL::INT,2)", "CK00001")]
    [InlineData("CREATE INDEX ix_expr ON t((COALESCE(n,1)))", "CS00003")]
    [InlineData("CREATE TABLE generated(n INT, g DECIMAL GENERATED ALWAYS AS (CASE WHEN n > 0 THEN n ELSE 2.5 END) STORED)", "CK00001")]
    public async Task Declare_UnsupportedForms_ShouldRetainRefusals(string sql, string code)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(n INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Execute(session, sql));

        // Assert
        failure.Diagnostics.First().Code.ShouldBe(code);
        database.Catalog.Tables.Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: dependent DDL validates before publication and reopens (#1788)")]
    public async Task Alter_Dependents_ShouldRetainOpenableDefinitions()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-common-dependencies", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("dependent-types", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await Execute(session, "CREATE TABLE t(n INT DEFAULT(COALESCE(1, 2.5)), CHECK(COALESCE(n, 1) > 0))");
                await Execute(session, "CREATE FUNCTION f() RETURNS INT IMMUTABLE RETURN 1");
                await Execute(session, "CREATE TABLE called(n INT DEFAULT(COALESCE(f(), 1)))");
                await Execute(session, "CREATE TABLE defaults(n INT DEFAULT(COALESCE(1, '2')))");
                await Execute(session, "CREATE VIEW v AS SELECT COALESCE(n, 1) AS n FROM t");
                await Execute(session, "CREATE VIEW dependent AS SELECT COALESCE(n, 2) AS n FROM v");

                // Act / Assert: views block TYPE; without them the CHECK is rebound against TEXT.
                (await Refuse("ALTER TABLE t ALTER n TYPE TEXT")).ShouldBe("CS03302");
                (await Refuse("CREATE OR REPLACE VIEW v AS SELECT COALESCE(n, 1.5) AS n FROM t")).ShouldBe("CS03033");
                await Execute(session, "CREATE OR REPLACE VIEW v AS SELECT COALESCE(n, 3) AS n FROM t");
                (await Refuse("DROP FUNCTION f()")).ShouldBe("CS03302");
                await Execute(session, "DROP VIEW dependent");
                await Execute(session, "DROP VIEW v");
                (await Refuse("ALTER TABLE t ALTER n TYPE TEXT")).ShouldBe("CS02080");
                (await Refuse("ALTER TABLE defaults ALTER n TYPE UUID USING NULL")).ShouldBe("CS02064");
                await Execute(session, "ALTER TABLE t ALTER n TYPE DECIMAL");
                await Execute(session, "CREATE INDEX ix ON t(n) WHERE COALESCE(n, 1) > 0");
                (await Refuse("ALTER TABLE t ALTER n TYPE REAL")).ShouldBe("CS03083");
                await Execute(session, "INSERT INTO t DEFAULT VALUES");
                await Execute(session, "INSERT INTO called DEFAULT VALUES");
                await Execute(session, "INSERT INTO defaults DEFAULT VALUES");

                async Task<string> Refuse(string sql) => (await Should.ThrowAsync<DatabaseException>(() => Execute(session, sql))).Diagnostics.Single().Code!;
            }
            await using var reopened = SqlDatabaseEngine.Create("dependent-types", new() { RootPath = root });
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await opened.CreateSessionAsync(CancellationToken.None);
            (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT n FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1m);
            (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT n FROM called")).ShouldHaveSingleItem()[0].ShouldBe(1);
            (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT n FROM defaults")).ShouldHaveSingleItem()[0].ShouldBe(1);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private static async Task Execute(SqlDatabaseSession session, string sql)
        => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
}
