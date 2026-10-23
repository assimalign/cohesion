using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNWayJoinTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - USING: ORDER BY aliases after wildcards use visible output positions")]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    public async Task Join_UsingWildcardOrdering_ShouldBindVisibleOutputSlots(string join)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL);");
        await ExecAsync(session, "CREATE TABLE b (id INT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES (2), (1);");
        await ExecAsync(session, "INSERT INTO b VALUES (1), (2);");
        string from = $" FROM a {join} b USING (id) ";

        // Act / Assert
        Check(await RowsAsync(session, "SELECT *, a.id AS sort, -a.id AS tail" + from + "ORDER BY sort;"),
            [[1, 1, -1L], [2, 2, -2L]]);
        Check(await RowsAsync(session, "SELECT *, a.id AS sort" + from + "ORDER BY sort;"), [[1, 1], [2, 2]]);
        Check(await RowsAsync(session, "SELECT *, -a.id AS sort" + from + "ORDER BY 2;"), [[2, -2L], [1, -1L]]);
        Check(await RowsAsync(session, "SELECT *, a.id AS sort, *, -a.id AS tail" + from + "ORDER BY tail;"),
            [[2, 2, 2, -2L], [1, 1, 1, -1L]]);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT *, a.id AS sort" + from + "ORDER BY 3;", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE018");
        var qualified = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT a.*, a.id AS sort" + from + "ORDER BY sort;", cancellationToken: CancellationToken.None));
        qualified.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LEFT: UNKNOWN, ON filters and WHERE filters retain SQL semantics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_LeftOnAndWhere_ShouldNullExtendOnlyUnmatched(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("left", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, indexed);

        // Act / Assert
        Check(await RowsAsync(session, "SELECT a.id, c.id FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id AND c.enabled = TRUE ORDER BY a.id;"),
            [[1, 1], [2, null], [3, null]]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(indexed ? ["join-seek:ix_b", "join-seek:ix_c"] : ["join-hash", "join-hash"]);
        Check(await RowsAsync(session, "SELECT a.id, c.id FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id WHERE c.enabled = TRUE ORDER BY a.id;"), [[1, 1]]);
        Check(await RowsAsync(session, "SELECT COUNT(*), COUNT(c.id) FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id AND c.enabled = TRUE;"), [[3L, 1L]]);
        Check(await RowsAsync(session, "SELECT a.id, c.id FROM a LEFT JOIN c ON NULL ORDER BY a.id;"), [[1, null], [2, null], [3, null]]);
        await using var result = (await session.ExecuteAsync("SELECT a.id, c.id FROM a LEFT JOIN c ON a.id = c.id;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns.Select(c => c.IsNullable).ShouldBe([false, true]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - N-way: grouping, DISTINCT, ordering and pagination compose")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_ThreeInputs_ShouldComposeRelationalClauses(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("nway", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, indexed);
        const string from = " FROM a JOIN b ON a.id = b.id LEFT JOIN c ON b.id = c.id ";

        // Act / Assert
        Check(await RowsAsync(session, "SELECT a.id, COUNT(c.id) AS n" + from + "GROUP BY a.id HAVING COUNT(*) = 1 ORDER BY a.id DESC LIMIT 2 OFFSET 1;"), [[2, 1L], [1, 1L]]);
        Check(await RowsAsync(session, "SELECT DISTINCT c.enabled" + from + "ORDER BY c.enabled LIMIT 2 OFFSET 1;"), [[false], [true]]);
        Check(await RowsAsync(session, "SELECT a.id FROM a, b JOIN c ON b.id = c.id CROSS JOIN a d WHERE a.id = 3 AND d.id = 1 ORDER BY b.id;"), [[3], [3]]);
        await using var description = SqlPlanner.Describe(new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT *" + from).Statement.SqlExpression));
        (await ReadAsync(description)).Count(row => Equals(row[2], "JoinStep")).ShouldBe(2);
        if (indexed) { (await ReadAsync(description)).Count(row => Equals(row[2], "IndexSeek")).ShouldBe(2); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: merged columns precede remaining left and right columns")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_Using_ShouldMergeNamesAndKeepQualifiedColumns(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE l (extra TEXT, id INT NOT NULL, name TEXT COLLATE case_insensitive);");
        await ExecAsync(session, "CREATE TABLE r (id INT NOT NULL, name TEXT COLLATE case_insensitive, other TEXT);");
        await ExecAsync(session, "INSERT INTO l VALUES ('L', 1, 'Ada'), ('unmatched', 2, 'Grace');");
        await ExecAsync(session, "INSERT INTO r VALUES (1, 'ADA', 'R');");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_r ON r(id, name);"); }

        // Act
        await using var result = (await session.ExecuteAsync("SELECT * FROM l LEFT JOIN r USING (name, id) ORDER BY id;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        result.Columns.Select(c => c.Name).ShouldBe(["name", "id", "extra", "other"]);
        Check(rows, [["Ada", 1, "L", "R"], ["Grace", 2, "unmatched", null]]);
        result.Columns[1].IsNullable.ShouldBeFalse();
        result.Columns[3].IsNullable.ShouldBeTrue();
        Check(await RowsAsync(session, "SELECT id, l.id, r.id, name, l.name, r.name FROM l LEFT JOIN r USING (id, name) ORDER BY id;"),
            [[1, 1, 1, "Ada", "Ada", "ADA"], [2, 2, null, "Grace", "Grace", null]]);
        Check(await RowsAsync(session, "SELECT id FROM l LEFT JOIN r USING (id) JOIN l again USING (id) ORDER BY id;"), [[1], [2]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CROSS: comma and explicit forms return products and typed empties")]
    [InlineData("a CROSS JOIN b", 9)]
    [InlineData("a, b", 9)]
    [InlineData("a CROSS JOIN b CROSS JOIN c", 18)]
    [InlineData("a, b, c", 18)]
    [InlineData("a LEFT JOIN c ON a.id = c.id CROSS JOIN b", 9)]
    public async Task Join_Products_ShouldReturnExactCardinality(string from, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cross", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, false);

        // Act / Assert
        (await RowsAsync(session, "SELECT a.id FROM " + from)).Count.ShouldBe(count);
        await ExecAsync(session, "DELETE FROM b;");
        await using var empty = (await session.ExecuteAsync("SELECT a.id FROM " + from, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadAsync(empty)).ShouldBeEmpty();
        empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int32);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JOIN: invalid scopes and USING fail binding without reading rows")]
    [InlineData("SELECT a.id FROM a JOIN b ON c.id = b.id JOIN c ON b.id = c.id;", "c.id", "COHSQLE015")]
    [InlineData("SELECT a.id FROM a, b JOIN c ON a.id = c.id;", "a.id", "COHSQLE015")]
    [InlineData("SELECT a.id FROM a JOIN b ON a.id = b.id JOIN c a ON b.id = a.id;", "a", "COHSQLE019")]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id JOIN c USING (id);", "id", "COHSQLE015")]
    [InlineData("SELECT * FROM a JOIN b USING (missing);", "missing", "COHSQLE015")]
    [InlineData("SELECT * FROM a JOIN c USING (enabled);", "enabled", "COHSQLE015")]
    [InlineData("SELECT * FROM c JOIN a USING (enabled);", "enabled", "COHSQLE015")]
    [InlineData("SELECT * FROM a JOIN b USING (id, id);", "id", "COHSQLE015")]
    [InlineData("SELECT a.id FROM a x LEFT JOIN b USING (id);", "a.id", "COHSQLE015")]
    [InlineData("SELECT bogus.id FROM a CROSS JOIN b;", "bogus.id", "COHSQLE015")]
    [InlineData("SELECT a.id FROM a x, b;", "a.id", "COHSQLE015")]
    public async Task Join_InvalidScope_ShouldFailBeforeReading(string sql, string reference, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("scope", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, false);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.Single().Code.ShouldBe(code);
        error.Message.ShouldContain(reference);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System joins: LEFT and N-way inputs share the statement catalog image")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Join_SystemSources_ShouldNullExtendAndCaptureOneImage(int isolation)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("system-left", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE names (name TEXT);");
        await ExecAsync(session, "INSERT INTO names VALUES ('names'), ('absent');");
        if (isolation > 0) { await session.BeginTransactionAsync(isolation == 1 ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, CancellationToken.None); }

        // Act / Assert
        Check(await RowsAsync(session, "SELECT n.name, c.COLUMN_NAME FROM names n LEFT JOIN INFORMATION_SCHEMA.COLUMNS c ON n.name = c.TABLE_NAME ORDER BY n.name;"),
            [["absent", null], ["names", "name"]]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-hash"]);
        session.LastStatementMetrics.CatalogSnapshot.ShouldNotBeNull();
        Check(await RowsAsync(session, "SELECT t.TABLE_NAME, c.COLUMN_NAME, n.name FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c USING (TABLE_NAME) LEFT JOIN names n ON t.TABLE_NAME = n.name WHERE t.TABLE_NAME = 'names';"),
            [["names", "name", "names"]]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-hash", "join-hash"]);
        session.LastStatementMetrics.CatalogSnapshotCaptures.ShouldBe(isolation == 2 ? 0 : 1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - LEFT: only the preserved input drives and composite probes use all earlier inputs")]
    public async Task Join_CompositeNWayProbe_ShouldPreserveLeftRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("composite-nway", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (tenant INT);");
        await ExecAsync(session, "CREATE TABLE b (item INT);");
        await ExecAsync(session, "CREATE TABLE c (tenant INT, item INT);");
        await ExecAsync(session, "INSERT INTO a VALUES (1), (2);");
        await ExecAsync(session, "INSERT INTO b VALUES (10), (20);");
        await ExecAsync(session, "INSERT INTO c VALUES (1, 10), (1, 30);");
        await ExecAsync(session, "CREATE INDEX ix_a ON a(tenant);");

        // Act / Assert: an index on the preserved side cannot reverse a LEFT join.
        Check(await RowsAsync(session, "SELECT a.tenant, c.tenant FROM a LEFT JOIN c ON a.tenant = c.tenant ORDER BY a.tenant;"),
            [[1, 1], [1, 1], [2, null]]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-scan"]);
        await ExecAsync(session, "CREATE INDEX ix_c ON c(tenant, item);");
        Check(await RowsAsync(session, "SELECT a.tenant, b.item, c.item FROM a CROSS JOIN b LEFT JOIN c ON c.tenant = a.tenant AND c.item = b.item ORDER BY a.tenant, b.item;"),
            [[1, 10, 10], [1, 20, null], [2, 10, null], [2, 20, null]]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-scan", "join-seek:ix_c"]);
        session.LastStatementMetrics.RecordsExamined.ShouldBe(5);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: INT and DECIMAL share decimal values in either input order")]
    [InlineData(false, "JOIN", false)]
    [InlineData(false, "LEFT JOIN", false)]
    [InlineData(true, "JOIN", false)]
    [InlineData(true, "LEFT JOIN", false)]
    [InlineData(false, "JOIN", true)]
    [InlineData(false, "LEFT JOIN", true)]
    [InlineData(true, "JOIN", true)]
    [InlineData(true, "LEFT JOIN", true)]
    public async Task Join_UsingDecimalTypes_ShouldCoerceMergedExpressions(bool reverse, string join, bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-type", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL);");
        await ExecAsync(session, "CREATE TABLE b (id DECIMAL NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES (1);");
        await ExecAsync(session, "INSERT INTO b VALUES (1);");
        string left = reverse ? "b" : "a", right = reverse ? "a" : "b";
        await ExecAsync(session, $"INSERT INTO {left} VALUES (3);");
        if (indexed)
        {
            await ExecAsync(session, "CREATE INDEX ix_a ON a(id);");
            await ExecAsync(session, "CREATE INDEX ix_b ON b(id);");
        }
        string from = $" FROM {left} {join} {right} USING (id) ";

        // Act
        await using var result = (await session.ExecuteAsync("SELECT *, id / 2 AS ratio, a.id, b.id" + from + "ORDER BY ratio;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(c => c.Type).ShouldBe([DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Decimal]);
        var expected = new List<object?[]> { new object?[] { 1m, 0.5m, 1, 1m } };
        if (join == "LEFT JOIN") { expected.Add(reverse ? [3m, 1.5m, null, 3m] : [3m, 1.5m, 3, null]); }
        Check(await ReadAsync(result), expected.ToArray());
        Check(await RowsAsync(session, "SELECT id / 2, COUNT(*)" + from + "WHERE id / 2 = 0.5 GROUP BY id ORDER BY id;"), [[0.5m, 1L]]);
        Check(await RowsAsync(session, "SELECT id / 2" + from + "JOIN a again USING (id) ORDER BY id;"),
            join == "LEFT JOIN" && !reverse ? [[0.5m], [1.5m]] : [[0.5m]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: BIGINT merged names choose the BIGINT overload and preserve qualified types")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_UsingBigInt_ShouldSelectCommonOverload(bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-bigint", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL);");
        await ExecAsync(session, "CREATE TABLE b (id BIGINT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES (1), (2);");
        await ExecAsync(session, "INSERT INTO b VALUES (1), (2);");
        await ExecAsync(session, "CREATE FUNCTION kind(p INT) RETURNS TEXT IMMUTABLE STRICT RETURN 'int';");
        await ExecAsync(session, "CREATE FUNCTION kind(p BIGINT) RETURNS TEXT IMMUTABLE STRICT RETURN 'bigint';");

        // Act
        await using var result = (await session.ExecuteAsync("SELECT *, a.id, b.id, kind(id), kind(a.id), kind(b.id) FROM " +
            (reverse ? "b LEFT JOIN a" : "a LEFT JOIN b") + " USING (id) ORDER BY 1;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(c => c.Type).Take(3).ShouldBe([DatabaseType.Int64, DatabaseType.Int32, DatabaseType.Int64]);
        Check(await ReadAsync(result), [[1L, 1, 1L, "bigint", "int", "bigint"], [2L, 2, 2L, "bigint", "int", "bigint"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: CHAR and VARCHAR clear differing modifiers without altering qualified columns")]
    [InlineData(false, "JOIN")]
    [InlineData(true, "JOIN")]
    [InlineData(false, "LEFT JOIN")]
    [InlineData(true, "LEFT JOIN")]
    public async Task Join_UsingCharacterTypes_ShouldClearMergedLength(bool reverse, string join)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-character", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id CHAR(3) NOT NULL);");
        await ExecAsync(session, "CREATE TABLE b (id VARCHAR(10) NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES ('a');");
        await ExecAsync(session, "INSERT INTO b VALUES ('a');");

        // Act
        await using var result = (await session.ExecuteAsync("SELECT *, id, a.id, b.id FROM " +
            (reverse ? $"b {join} a" : $"a {join} b") + " USING (id);",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        var mergedType = reverse ? DatabaseType.String : DatabaseType.Character;
        result.Columns.Select(c => c.Type).ShouldBe([mergedType, mergedType, DatabaseType.Character, DatabaseType.String]);
        result.Columns.Select(c => c.MaxLength).ShouldBe(new int?[] { null, null, 3, 10 });
        string merged = reverse ? "a" : "a  ";
        Check(await ReadAsync(result), [[merged, merged, "a  ", "a"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: identical domains retain casts while mixed inputs use the base type")]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    public async Task Join_UsingDomainTypes_ShouldPreserveOnlyAnIdenticalDomain(string join)
    {
        // Arrange
        var domain = SqlType.CreateDomain("join_id", SqlType.Integer);
        var builder = SqlDatabaseEngine.CreateBuilder("using-domain");
        builder.Types.Add(domain).AddCast(SqlCast.Create<int, string>(domain, SqlType.Text,
            static value => "domain", SqlCastContext.Explicit));
        builder.Types.Add(SqlType.CreateDomain("join_code", SqlType.Char(3)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id join_id);");
        await ExecAsync(session, "CREATE TABLE b (id join_id);");
        await ExecAsync(session, "CREATE TABLE c (id INT);");
        await ExecAsync(session, "INSERT INTO a VALUES (1);");
        await ExecAsync(session, "INSERT INTO b VALUES (1);");
        await ExecAsync(session, "INSERT INTO c VALUES (1);");

        // Act / Assert
        Check(await RowsAsync(session, $"SELECT CAST(id AS TEXT), CAST(a.id AS TEXT), CAST(b.id AS TEXT) FROM a {join} b USING (id);"),
            [["domain", "domain", "domain"]]);
        Check(await RowsAsync(session, $"SELECT CAST(id AS TEXT), CAST(a.id AS TEXT), CAST(c.id AS TEXT) FROM a {join} c USING (id);"),
            [["1", "domain", "1"]]);
        await ExecAsync(session, "CREATE TABLE codes (id join_code);");
        await ExecAsync(session, "CREATE TABLE texts (id CHAR(3));");
        await using var lengths = (await session.ExecuteAsync($"SELECT *, codes.id, texts.id FROM codes {join} texts USING (id);",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        lengths.Columns.Select(column => column.MaxLength).ShouldBe(new int?[] { null, 3, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - USING: incompatible types fail planning even on empty inputs")]
    [InlineData("JOIN")]
    [InlineData("LEFT JOIN")]
    public async Task Join_UsingIncompatibleTypes_ShouldRefuseBeforeExecution(string join)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("using-incompatible", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT);");
        await ExecAsync(session, "CREATE TABLE b (id TEXT);");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"SELECT * FROM a {join} b USING (id);", cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE015");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - N-way: many empty and populated inputs do not recurse through the executor stack")]
    public async Task Join_ManyInputs_ShouldExecuteWithoutRecursing()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("many-joins", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t (id INT);");
        string sql = "SELECT a0.id FROM t a0 " + string.Join(" ", Enumerable.Range(1, 1024).Select(i => $"CROSS JOIN t a{i}"));

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldBeEmpty();
        session.LastStatementMetrics!.JoinAccessPaths.Count.ShouldBe(1024);
        await ExecAsync(session, "INSERT INTO t VALUES (1);");
        Check(await RowsAsync(session, sql), [[1]]);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(1025);
    }

    private static async Task SeedAsync(SqlDatabaseSession session, bool indexed)
    {
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL);");
        await ExecAsync(session, "CREATE TABLE b (id INT NOT NULL);");
        await ExecAsync(session, "CREATE TABLE c (id INT NOT NULL, enabled BOOLEAN);");
        await ExecAsync(session, "INSERT INTO a VALUES (1), (2), (3);");
        await ExecAsync(session, "INSERT INTO b VALUES (1), (2), (3);");
        await ExecAsync(session, "INSERT INTO c VALUES (1, TRUE), (2, FALSE);");
        if (indexed)
        {
            await ExecAsync(session, "CREATE INDEX ix_b ON b(id);");
            await ExecAsync(session, "CREATE INDEX ix_c ON c(id);");
        }
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static void Check(IReadOnlyList<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < actual.Count; i++) { actual[i].ShouldBe(expected[i]); }
    }
}
