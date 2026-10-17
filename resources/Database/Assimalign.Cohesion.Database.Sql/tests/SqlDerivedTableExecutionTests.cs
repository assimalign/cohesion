using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDerivedTableExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: clauses joins and nesting compose")]
    [InlineData("SELECT d.renamed FROM (SELECT id FROM t WHERE id > 1 ORDER BY id DESC LIMIT 1 OFFSET 1) d (renamed)", 2)]
    [InlineData("SELECT d.id FROM (SELECT id FROM t GROUP BY id HAVING COUNT(*) = 1) d WHERE d.id = 2", 2)]
    [InlineData("SELECT d.id FROM t base JOIN (SELECT id FROM t) d ON base.id = d.id WHERE base.id = 2", 2)]
    [InlineData("SELECT d.id FROM t base, (SELECT id FROM t) d WHERE base.id = d.id AND d.id = 2", 2)]
    [InlineData("SELECT b.id FROM (SELECT id FROM t) a JOIN (SELECT * FROM (SELECT id FROM t) nested) b ON a.id = b.id WHERE a.id = 2", 2)]
    [InlineData("SELECT CAST(SUM(d.id) AS BIGINT) FROM (SELECT id FROM t WHERE id > 1) d", 5L)]
    [InlineData("SELECT COUNT(*) FROM (SELECT id FROM t WHERE id < 0) d", 0L)]
    [InlineData("SELECT (SELECT MAX(id) FROM (SELECT id FROM t) inner_d) FROM (SELECT id FROM t LIMIT 1) outer_d", 3)]
    [InlineData("SELECT t.id FROM t JOIN (SELECT id FROM t) d ON t.id = d.id ORDER BY t.id DESC LIMIT 1", 3)]
    [InlineData("SELECT t.id AS n FROM t JOIN (SELECT id FROM t) d ON t.id = d.id ORDER BY n DESC LIMIT 1", 3)]
    [InlineData("SELECT t.id FROM t JOIN (SELECT id FROM t) d ON t.id = d.id ORDER BY 1 DESC LIMIT 1", 3)]
    [InlineData("SELECT t.id FROM t JOIN (SELECT CAST(id AS BIGINT) AS id FROM t) d USING (id) ORDER BY t.id DESC LIMIT 1", 3)]
    [InlineData("SELECT * FROM (SELECT t.id FROM t JOIN (SELECT id FROM t) x ON t.id = x.id ORDER BY t.id DESC LIMIT 1) d", 3)]
    [InlineData("SELECT * FROM (SELECT * FROM (SELECT id FROM t) x ORDER BY id DESC LIMIT 1) d", 3)]
    public async Task Execute_DerivedRelation_ShouldApplyRelationalOperators(string sql, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([expected]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: empty metadata and collations survive renaming")]
    public async Task Execute_DerivedOutputs_ShouldPreserveMetadataAndCollations()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-types", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (name VARCHAR(12) COLLATE case_insensitive, amount DECIMAL(9,2));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES ('Ada', 2.50);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE plain_names (name TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO plain_names VALUES ('ADA');", cancellationToken: CancellationToken.None);

        // Act
        await using var empty = (DatabaseResultSet)await session.ExecuteAsync("SELECT d.label, d.cost FROM (SELECT name, amount FROM t WHERE 1 = 0) d (label, cost);", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT d.label FROM (SELECT name FROM t) d (label) WHERE d.label = 'ADA';");
        var joined = await RowsAsync(session, "SELECT d.name FROM (SELECT name FROM plain_names) d JOIN t ON d.name = t.name;");
        var query = SqlQueryRequest.FromSql("SELECT * FROM (SELECT name, amount FROM t) d;");
        var plan = new SqlPlanner(database.Catalog, null).Plan(query.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var grouped = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT * FROM (SELECT amount FROM t GROUP BY amount) d;").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Assert
        empty.Columns.Select(c => c.Name).ShouldBe(["label", "cost"]);
        empty.Columns.Select(c => c.Type).ShouldBe([DatabaseType.String, DatabaseType.Decimal]);
        empty.Columns[0].MaxLength.ShouldBe(12);
        (await ReadAsync(empty)).ShouldBeEmpty();
        rows.ShouldHaveSingleItem().ShouldBe(["Ada"]);
        joined.ShouldHaveSingleItem().ShouldBe(["ADA"]);
        plan.Bindings[0].Source.Columns[1].Type.Precision.ShouldBe(9);
        plan.Bindings[0].Source.Columns[1].Type.Scale.ShouldBe(2);
        grouped.Bindings[0].Source.Columns[0].Type.Precision.ShouldBe(9);
        grouped.Bindings[0].Source.Columns[0].Type.Scale.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: invalid names and shapes fail before enumeration")]
    [InlineData("SELECT * FROM (SELECT id FROM t WHERE 1 = 0) d (a, b)", "COHSQLE013")]
    [InlineData("SELECT * FROM (SELECT id, id + 1 AS n FROM t) d (a)", "COHSQLE013")]
    [InlineData("SELECT * FROM (SELECT id AS \"\" FROM t) d", "COHSQLE013")]
    [InlineData("SELECT * FROM (SELECT id FROM t) d (\" \")", "COHSQLE013")]
    [InlineData("SELECT t.id FROM (SELECT id FROM t) d", "COHSQLE015")]
    [InlineData("SELECT bogus.x FROM (SELECT id FROM t) d (x)", "COHSQLE015")]
    [InlineData("SELECT d.id FROM (SELECT id, id FROM t) d", "COHSQLE015")]
    [InlineData("SELECT * FROM t, (SELECT missing FROM t) d", "COHSQLE015")]
    [InlineData("SELECT * FROM (SELECT nope FROM t) d", "COHSQLE015")]
    [InlineData("SELECT * FROM (SELECT x.id FROM t) d", "COHSQLE015")]
    [InlineData("SELECT * FROM (SELECT t.id FROM t hidden) d", "COHSQLE015")]
    public async Task Execute_InvalidDerivedRelation_ShouldRejectBeforeReading(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);

        // Act / Assert: planning alone fails, without starting a row enumerator.
        var error = Should.Throw<DatabaseException>(() => new SqlPlanner(database.Catalog, null)
            .Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        var executionError = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));
        executionError.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: default outputs match direct comparisons in either operand order")]
    [InlineData("UPPER(name)", 1)]
    [InlineData("'ADA'", 1)]
    [InlineData("CAST(name AS TEXT)", 1)]
    [InlineData("name COLLATE case_insensitive", 1)]
    public async Task Execute_DerivedCollation_ShouldMatchDirectExpression(string expression, int expectedCount)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-collation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE plain_names (name TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE ci_names (name TEXT COLLATE case_insensitive);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO plain_names VALUES ('ada');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO ci_names VALUES ('Ada');", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (bool reverse in new[] { false, true })
        {
            string directCondition = reverse ? $"c.name = {expression}" : $"{expression} = c.name";
            // Qualify the source operand without changing the expression's collation.
            directCondition = directCondition.Replace("(name", "(p.name", StringComparison.Ordinal)
                .Replace("= name", "= p.name", StringComparison.Ordinal);
            if (directCondition.StartsWith("name", StringComparison.Ordinal)) { directCondition = "p." + directCondition; }
            string condition = reverse ? "c.name = d.n" : "d.n = c.name";
            var direct = await RowsAsync(session, $"SELECT p.name FROM plain_names p JOIN ci_names c ON {directCondition}");
            var derived = await RowsAsync(session, $"SELECT d.n FROM (SELECT {expression} AS n FROM plain_names) d JOIN ci_names c ON {condition}");
            direct.Count.ShouldBe(expectedCount);
            derived.Count.ShouldBe(direct.Count);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: explicit binary beats inherited collation in either operand order")]
    [InlineData("d.n = p.name")]
    [InlineData("p.name = d.n")]
    public async Task Execute_ExplicitDerivedBinary_ShouldKeepDeclaredStrength(string condition)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("derived-binary");
        builder.AddDatabase("db", database => database.DefaultCollation = Collation.CaseInsensitive);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE names (name TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE ci_names (name TEXT COLLATE case_insensitive);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO names VALUES ('Ada');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO ci_names VALUES ('Ada');", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"SELECT d.n FROM (SELECT LOWER(name) COLLATE binary AS n FROM names) d JOIN names p ON {condition}");
        var direct = await RowsAsync(session, "SELECT b.name FROM names b JOIN names p ON " +
            condition.Replace("d.n", "LOWER(b.name) COLLATE binary", StringComparison.Ordinal));
        var declaredOther = await RowsAsync(session,
            "SELECT d.n FROM (SELECT LOWER(name) COLLATE binary AS n FROM names) d JOIN ci_names c ON d.n = c.name");

        // Assert
        rows.ShouldBeEmpty();
        direct.ShouldBeEmpty();
        declaredOther.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: unknown references retain their coded exact spans")]
    [InlineData("SELECT * FROM (SELECT nope FROM t) d", "nope")]
    [InlineData("SELECT * FROM (SELECT x.id FROM t) d", "x.id")]
    [InlineData("SELECT * FROM (SELECT t.id FROM t hidden) d", "t.id")]
    [InlineData("SELECT * FROM t, (SELECT idd FROM t) d", "idd")]
    [InlineData("SELECT * FROM t, (SELECT t.id FROM (SELECT id AS other FROM t) t) d", "t.id")]
    public async Task Execute_UnknownDerivedReference_ShouldReportLocatedUnknownColumn(string sql, string reference)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-unknown", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));

        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE015");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(reference);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: qualified and merged USING outputs deliver their declared numeric types")]
    [InlineData("a.id", "", DatabaseType.Int32, "integer")]
    [InlineData("a.id", "GROUP BY a.id", DatabaseType.Int32, "integer")]
    [InlineData("b.id", "", DatabaseType.Int64, "bigint")]
    [InlineData("b.id", "GROUP BY b.id", DatabaseType.Int64, "bigint")]
    [InlineData("id", "", DatabaseType.Int64, "bigint")]
    [InlineData("id", "GROUP BY id", DatabaseType.Int64, "bigint")]
    public async Task Execute_DerivedUsingOutput_ShouldKeepExpressionType(string expression, string grouping, DatabaseType type, string kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-using", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE a (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b (id BIGINT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO a VALUES (1);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO b VALUES (1);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION numeric_kind(x INT) RETURNS TEXT RETURN 'integer';", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION numeric_kind(x BIGINT) RETURNS TEXT RETURN 'bigint';", cancellationToken: CancellationToken.None);
        string body = $"SELECT {expression} FROM a JOIN b USING (id) {grouping}";

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT d.id FROM ({body}) d", cancellationToken: CancellationToken.None);
        var rows = await ReadAsync(result);
        var overload = await RowsAsync(session, $"SELECT numeric_kind(d.id) FROM ({body}) d");

        // Assert
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(type);
        object value = rows.ShouldHaveSingleItem()[0]!;
        if (type == DatabaseType.Int32) { value.ShouldBeOfType<int>().ShouldBe(1); }
        else { value.ShouldBeOfType<long>().ShouldBe(1L); }
        overload.ShouldHaveSingleItem().ShouldBe([kind]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: projected membership resolves the Boolean overload")]
    [InlineData("IN", true)]
    [InlineData("NOT IN", false)]
    public async Task Execute_DerivedMembership_ShouldHaveBooleanType(string predicate, bool expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-bool", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE FUNCTION bool_kind(x BOOLEAN) RETURNS TEXT RETURN 'boolean';", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION bool_kind(x INT) RETURNS TEXT RETURN 'integer';", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            $"SELECT d.a, bool_kind(d.a) FROM (SELECT id {predicate} (SELECT id FROM t) AS a FROM t WHERE id = 1) d", cancellationToken: CancellationToken.None);

        // Assert
        result.Columns[0].Type.ShouldBe(DatabaseType.Boolean);
        (await ReadAsync(result)).ShouldHaveSingleItem().ShouldBe([expected, "boolean"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: only real sibling references require LATERAL at their exact span")]
    [InlineData("SELECT * FROM u, (SELECT k FROM t) d", "k")]
    [InlineData("SELECT * FROM t, (SELECT t.id FROM u) d", "t.id")]
    [InlineData("SELECT * FROM t, (SELECT dbo.t.id FROM u) d", "dbo.t.id")]
    [InlineData("SELECT * FROM t, (SELECT \"t\" . \"id\" FROM u) d", "\"t\" . \"id\"")]
    [InlineData("SELECT * FROM (SELECT k FROM u) sibling, (SELECT sibling.k FROM t) d", "sibling.k")]
    public async Task Execute_DerivedSiblingReference_ShouldRequireLateral(string sql, string reference)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-sibling", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE u (k INT);", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        if (result is DatabaseResultSet unexpectedRows) { await unexpectedRows.DisposeAsync(); }

        // Assert
        result.Status.ShouldBe(DatabaseResultStatus.Error);
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message!.ShouldContain("LATERAL", Case.Sensitive);
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(reference);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: enclosing query references remain ordinary deferred correlation")]
    public async Task Execute_DerivedEnclosingReference_ShouldKeepCorrelationRefusal()
    {
        // Arrange: part (a) will turn this marker into an execution case.
        await using var engine = SqlDatabaseEngine.Create("derived-outer", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        const string sql = "SELECT * FROM t WHERE EXISTS (SELECT 1 FROM (SELECT u.id FROM t u WHERE u.id = t.id) d)";

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        if (result is DatabaseResultSet unexpectedRows) { await unexpectedRows.DisposeAsync(); }

        // Assert
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message!.ShouldContain("Correlated subqueries are not supported", Case.Sensitive);
        diagnostic.Message!.ShouldNotContain("LATERAL", Case.Sensitive);
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe("t.id");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived tables: output ordering retains the shared pin after binding")]
    [InlineData("SELECT q.years FROM (SELECT id AS years FROM t) q ORDER BY years")]
    [InlineData("SELECT q.years FROM (SELECT id AS years FROM t) q ORDER BY 1")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY years;")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY 1;")]
    [InlineData("SELECT * FROM (SELECT id FROM u) d ORDER BY id;")]
    [InlineData("SELECT q.years FROM (SELECT id AS years FROM t) q ORDER BY q.years")]
    [InlineData("SELECT q.id + 1 AS n FROM t JOIN (SELECT id FROM t) q ON t.id = q.id ORDER BY n")]
    [InlineData("SELECT q.id FROM t JOIN (SELECT id FROM t) q ON t.id = q.id ORDER BY 1")]
    [InlineData("SELECT q.id, COUNT(*) FROM t JOIN (SELECT id FROM t) q ON t.id = q.id GROUP BY q.id ORDER BY q.id")]
    [InlineData("SELECT id FROM t JOIN (SELECT CAST(id AS BIGINT) AS id FROM t) q USING (id) ORDER BY id")]
    public async Task Execute_DerivedOutputOrdering_ShouldRetainCapabilityRefusal(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, age INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 10);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE u (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO u VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        if (result is DatabaseResultSet unexpectedRows) { await unexpectedRows.DisposeAsync(); }

        // Assert
        var diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message!.ShouldContain("derived-table SUBQUERY", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: USING composes across materialization and preserves shared domains")]
    public async Task Execute_DerivedUsingInputs_ShouldPreserveMergedMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-domain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN units AS INT CHECK (VALUE > 0);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE a (id units);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b (id units);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO a VALUES (1);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO b VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        const string sql = "SELECT * FROM (SELECT id FROM a JOIN b USING (id)) d";
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var body = await RowsAsync(session, sql);
        var joined = await RowsAsync(session, "SELECT * FROM a JOIN (SELECT id FROM b) d USING (id)");
        var twoDerived = await RowsAsync(session, "SELECT * FROM (SELECT id FROM a) l JOIN (SELECT id FROM b) r USING (id)");

        // Assert
        plan.Bindings[0].Source.Columns.ShouldHaveSingleItem().DomainName.ShouldBe("units");
        plan.Bindings[0].Source.Columns[0].Type.Type.ShouldBe(DatabaseType.Int32);
        body.ShouldHaveSingleItem().ShouldBe([1]);
        joined.ShouldHaveSingleItem().ShouldBe([1]);
        twoDerived.ShouldHaveSingleItem().ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: merged default collation keeps inherited strength")]
    public async Task Execute_DerivedUsingText_ShouldKeepDefaultCollationStrength()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-using-text", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (name TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (name TEXT COLLATE case_insensitive);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES ('ada');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES ('Ada');", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string condition in new[] { "d.name = c.name", "c.name = d.name" })
        {
            var rows = await RowsAsync(session, $"SELECT d.name FROM (SELECT name FROM p a JOIN p b USING (name)) d JOIN c ON {condition}");
            rows.ShouldHaveSingleItem().ShouldBe(["ada"]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: system relations share auto-commit catalog capture")]
    public async Task Execute_SystemDerivedTable_ShouldJoinCapturedCatalog()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-catalog", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "SELECT d.name, t.id FROM (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES) d (name) JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_NAME = d.name JOIN t ON t.id = 2 WHERE d.name = 't';");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(["t", 2]);
        await using var explanation = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT * FROM (SELECT id FROM t) d;", cancellationToken: CancellationToken.None);
        var operators = await ReadAsync(explanation);
        operators.ShouldContain(row => Equals(row[2], "DerivedTable"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Derived tables: empty LEFT inputs and INSERT sources preserve shape")]
    public async Task Execute_EmptyDerivedAndInsertSource_ShouldPreserveTypedRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-insert", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE copied (id INT, absent INT);", cancellationToken: CancellationToken.None);

        // Act
        var insert = await session.ExecuteAsync("INSERT INTO copied SELECT t.id, d.id FROM t LEFT JOIN (SELECT id FROM t WHERE 1 = 0) d ON t.id = d.id;", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT id, absent FROM copied ORDER BY id;");

        // Assert
        insert.AffectedCount.ShouldBe(3);
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe([1, null]);
        rows[1].ShouldBe([2, null]);
        rows[2].ShouldBe([3, null]);
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2), (3);", cancellationToken: CancellationToken.None);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
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
}
