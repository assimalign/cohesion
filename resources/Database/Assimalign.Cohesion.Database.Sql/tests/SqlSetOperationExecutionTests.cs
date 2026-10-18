using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSetOperationExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set CHAR common types: an unconstrained typmod preserves membership")]
    [InlineData("UNION", 1)]
    [InlineData("INTERSECT", 1)]
    [InlineData("EXCEPT", 0)]
    public async Task Execute_CharacterFirst_ShouldPreserveValues(string operation, int expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE c (fixed CHAR(5), plain TEXT);");
        await ExecuteAsync(session, "INSERT INTO c VALUES ('ab', 'ab');");

        // Act
        var rows = await RowsAsync(session, $"SELECT fixed FROM c {operation} SELECT plain FROM c;");

        // Assert
        rows.Count.ShouldBe(expected);
        if (expected > 0) { rows[0][0].ShouldBe("ab   "); }
        (await RowsAsync(session, "SELECT plain FROM c WHERE plain IN (SELECT fixed FROM c UNION SELECT plain FROM c);")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT d.fixed FROM (SELECT fixed FROM c UNION SELECT plain FROM c) d UNION SELECT plain FROM c;"))
            .ShouldHaveSingleItem()[0].ShouldBe("ab   ");
        await ExecuteAsync(session, "CREATE TABLE target (plain TEXT);");
        (await ExecuteAsync(session, "INSERT INTO target SELECT fixed FROM c UNION SELECT plain FROM c;")).AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT plain FROM target;")).ShouldHaveSingleItem()[0].ShouldBe("ab");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set CHAR common types: differing lengths and NULL keep full text")]
    public async Task Execute_UnconstrainedCharacter_ShouldKeepTextLength()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE c (fixed CHAR(5), plain TEXT);");
        await ExecuteAsync(session, "INSERT INTO c VALUES ('ab', 'hello'), ('ab', 'hx');");

        // Act / Assert
        (await RowsAsync(session, "SELECT fixed FROM c UNION SELECT plain FROM c ORDER BY 1;"))
            .Select(row => row[0]).ShouldBe(new object?[] { "ab   ", "hello", "hx" });
        (await RowsAsync(session, "SELECT NULL FROM c UNION SELECT fixed FROM c UNION SELECT plain FROM c ORDER BY 1;"))
            .Select(row => row[0]).ShouldBe(new object?[] { null, "ab   ", "hello", "hx" });
        (await RowsAsync(session, "SELECT CAST('ab' AS CHAR(5)) FROM c UNION SELECT CAST('hello' AS CHAR(6)) FROM c ORDER BY 1;"))
            .Select(row => row[0]).ShouldBe(new object?[] { "ab   ", "hello " });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set chains: the maximum limit executes without recursive UNION frames")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_MaximumUnionChain_ShouldFlattenOnSmallStack(bool all)
    {
        // Arrange
        const int count = SqlQueryParserOptions.MaximumExpressionNestingLimit;
        await using var engine = SqlDatabaseEngine.Create("deep-sets", new() { ExpressionNestingLimit = count });
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = count });
        var statement = parser.Parse(string.Join(all ? " UNION ALL " : " UNION ", Enumerable.Repeat("SELECT id FROM t", count)))
            .ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        statement.ExpressionNestingDepth.ShouldBe(count);
        using var thread = new SqlDepthTestThread(512);

        // Act
        var (rows, failure) = thread.Run(() =>
        {
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(statement.SqlExpression)
                .ShouldBeOfType<SqlSetOperationPlan>();
            plan.Operands.Count.ShouldBe(count);
            return ReadTyped();
        });
        int ReadTyped()
        {
            var result = session.ExecuteAsync(new SqlQueryRequest(statement), cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult()
                .ShouldBeAssignableTo<DatabaseResultSet>();
            try { return ReadAsync(result).GetAwaiter().GetResult().Count; }
            finally { result.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        // Assert
        failure.ShouldBeNull();
        rows.ShouldBe(all ? count : 1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Mixed set chains: recursive execution fails with COHSQLE004 on an explicit small stack")]
    public async Task Execute_DeepMixedChain_ShouldGuardStack()
    {
        // Arrange
        const int count = SqlQueryParserOptions.MaximumExpressionNestingLimit;
        await using var engine = SqlDatabaseEngine.Create("mixed-sets", new() { ExpressionNestingLimit = count });
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
        string sql = "SELECT id FROM t" + string.Concat(Enumerable.Range(1, count - 1)
            .Select(index => index % 2 == 0 ? " UNION SELECT id FROM t" : " EXCEPT SELECT id FROM t"));
        var statement = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = count }).Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        statement.ExpressionNestingDepth.ShouldBe(count);
        using var thread = new SqlDepthTestThread(512);

        // Act
        var (_, failure) = thread.Run(() => session.ExecuteAsync(new SqlQueryRequest(statement), cancellationToken: CancellationToken.None)
            .AsTask().GetAwaiter().GetResult());

        // Assert
        failure.ShouldBeAssignableTo<DatabaseException>()!.Diagnostics!.ShouldContain(item => item.Code == "COHSQLE004");
        (await RowsAsync(session, "SELECT id FROM t;")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set operations: duplicate and NULL multiplicities follow SQL bags")]
    [InlineData("UNION ALL", new int[] { 0, 0, 0, 1, 1, 1, 2, 2, 3 })]
    [InlineData("UNION", new int[] { 0, 1, 2, 3 })]
    [InlineData("UNION DISTINCT", new int[] { 0, 1, 2, 3 })]
    [InlineData("INTERSECT", new int[] { 0, 1 })]
    [InlineData("INTERSECT DISTINCT", new int[] { 0, 1 })]
    [InlineData("INTERSECT ALL", new int[] { 0, 1 })]
    [InlineData("EXCEPT", new int[] { 2 })]
    [InlineData("EXCEPT DISTINCT", new int[] { 2 })]
    [InlineData("EXCEPT ALL", new int[] { 0, 1, 2, 2 })]
    public async Task Execute_Bags_ShouldApplyMultiplicity(string operation, int[] expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, $"SELECT id FROM a {operation} SELECT id FROM b ORDER BY 1;");

        // Assert
        rows.Select(row => row[0] ?? 0).ShouldBe(expected.Cast<object>());
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set operations: output ordering and windows apply after combination")]
    [InlineData("ORDER BY id LIMIT 2 OFFSET 1")]
    [InlineData("ORDER BY 1 OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public async Task Execute_CombinedWindow_ShouldOrderOutput(string tail)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, $"SELECT id FROM a UNION SELECT id FROM b {tail};");

        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await RowsAsync(session, "(SELECT id FROM a WHERE id > 0 ORDER BY id LIMIT 1) UNION ALL (SELECT id FROM b WHERE id > 0 ORDER BY id DESC LIMIT 1) ORDER BY 1;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 3 });
        (await RowsAsync(session, "SELECT id FROM a WHERE id = 2 UNION SELECT TOP 1 id FROM b WHERE id = 3 OFFSET 1;")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: IN EXISTS scalar derived CTE and INSERT sources execute")]
    public async Task Execute_QueryPositions_ShouldUseRelationContract()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE target (id BIGINT);");

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM a WHERE id IN (SELECT id FROM a UNION SELECT id FROM b) ORDER BY id;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 1, 2, 2 });
        (await RowsAsync(session, "SELECT id FROM b WHERE id NOT IN (SELECT id FROM a UNION SELECT id FROM a);")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT id FROM b WHERE EXISTS (SELECT id FROM a EXCEPT SELECT id FROM b);")).Count.ShouldBe(3);
        (await RowsAsync(session, "SELECT id FROM b WHERE EXISTS (SELECT id FROM a EXCEPT SELECT id FROM a);")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT (SELECT id FROM a WHERE id = 1 UNION SELECT id FROM b WHERE id = 1) FROM b LIMIT 1;"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        var cardinality = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT (SELECT id FROM a UNION SELECT id FROM b) FROM b;"));
        cardinality.Message.ShouldBe("Scalar subquery returned more than one row; at most one row is allowed.");
        (await RowsAsync(session, "SELECT d.id FROM (SELECT id FROM a UNION SELECT id FROM b) d;"))
            .Select(row => row[0]).OrderBy(value => value).ShouldBe(new object?[] { null, 1, 2, 3 });
        (await RowsAsync(session, "SELECT outer_b.id FROM b outer_b WHERE EXISTS (SELECT id FROM a WHERE id = outer_b.id EXCEPT SELECT id FROM b WHERE id = 3) ORDER BY 1;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1 });
        (await RowsAsync(session, "WITH q AS (SELECT id FROM a ORDER BY id LIMIT 1) SELECT id FROM b WHERE id < 0 UNION ALL SELECT id FROM q;"))
            .ShouldHaveSingleItem()[0].ShouldBeNull();
        (await RowsAsync(session, "WITH q AS (SELECT 1 AS x UNION ALL SELECT 2) SELECT x FROM q ORDER BY x;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        (await ExecuteAsync(session, "INSERT INTO target (SELECT id FROM a UNION ALL SELECT id FROM b);")).AffectedCount.ShouldBe(9);
        (await ExecuteAsync(session, "INSERT INTO target (id) (SELECT id FROM a WHERE id = 2 UNION SELECT id FROM b WHERE id = 3);")).AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT id FROM target WHERE id = 1;")).Count.ShouldBe(3);
        await ExecuteAsync(session, "CREATE TABLE constrained (id INT NOT NULL UNIQUE);");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "INSERT INTO constrained SELECT id FROM a WHERE id = 2 UNION ALL SELECT id FROM b WHERE id IS NULL;"));
        (await RowsAsync(session, "SELECT id FROM constrained;")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set operations: compatibility and output ordering fail at plan time on empty inputs")]
    [InlineData("SELECT id FROM a WHERE id < 0 UNION SELECT id, id FROM b WHERE id < 0;", "COHSQLE013", "1", "2")]
    [InlineData("SELECT id FROM a WHERE id < 0 UNION SELECT CAST(id AS TEXT) FROM b WHERE id < 0;", "COHSQLE003", "Int32", "String")]
    [InlineData("SELECT id, id FROM a UNION SELECT id, CAST('1' AS TEXT) FROM b;", "COHSQLE003", "column 2", "String")]
    [InlineData("INSERT INTO a SELECT id FROM a UNION SELECT id, id FROM b;", "COHSQLE013", "1", "2")]
    [InlineData("INSERT INTO a SELECT id FROM a UNION SELECT CAST('1' AS TEXT) FROM b;", "COHSQLE003", "column 1", "String")]
    [InlineData("SELECT id FROM a WHERE id < 0 UNION SELECT 'invalid' FROM b WHERE id < 0;", "COHSQLE022", "INTEGER", "numeric text")]
    [InlineData("SELECT id FROM a UNION SELECT id FROM b ORDER BY id + 1;", "COHSQLE018", "output", "ordinal")]
    [InlineData("SELECT id FROM a UNION SELECT id FROM b ORDER BY a.id;", "COHSQLE018", "output", "qualified")]
    [InlineData("SELECT id FROM a UNION SELECT id FROM b ORDER BY 9;", "COHSQLE018", "output", "ordinal")]
    [InlineData("SELECT id FROM a UNION SELECT ABS() FROM b;", "COHSQLE006", "ABS", "argument")]
    public async Task Plan_InvalidCombinedOutput_ShouldRejectBeforeRows(string sql, string code, string first, string second)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var exception = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

        // Assert
        exception.Message.ShouldContain(first);
        exception.Message.ShouldContain(second);
        exception.Diagnostics.ShouldContain(item => item.Code == code);
        if (code == "COHSQLE003") { exception.Message.ShouldContain(sql.Contains("id, id") ? "column 2" : "column 1", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set operations: values coerce before duplicate comparison")]
    [InlineData("SELECT id AS first_name FROM a WHERE id = 1 UNION SELECT CAST(id AS BIGINT) AS other FROM b WHERE id = 1", DatabaseType.Int64, 1L)]
    [InlineData("SELECT CAST(0.1 AS DECIMAL) FROM a UNION SELECT CAST(0.1 AS DOUBLE PRECISION) FROM b", DatabaseType.Float64, 0.1d)]
    [InlineData("SELECT CAST('2026-01-01' AS DATE) FROM a UNION SELECT CAST('2026-01-01 00:00:00' AS TIMESTAMP) FROM b", DatabaseType.DateTime, "date")]
    [InlineData("SELECT CAST('ab' AS TEXT) FROM a UNION SELECT CAST('ab' AS CHAR(5)) FROM b", DatabaseType.String, "ab")]
    [InlineData("SELECT NULL FROM a UNION SELECT NULL FROM b", DatabaseType.String, null)]
    [InlineData("SELECT id FROM a WHERE id = 1 UNION SELECT '1' FROM b", DatabaseType.Int32, 1)]
    public async Task Execute_CommonType_ShouldCoerceBeforeDeduplication(string sql, DatabaseType type, object? expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        result.Columns[0].Type.ShouldBe(type);
        var actual = rows.ShouldHaveSingleItem()[0];
        actual.ShouldBe(Equals(expected, "date") ? new DateTime(2026, 1, 1) : expected);
        if (sql.Contains("first_name")) { result.Columns[0].Name.ShouldBe("first_name"); actual.ShouldBeOfType<long>(); }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: collation priority controls sets and IN comparison")]
    public async Task Execute_CollationPriority_ShouldUseUnifiedOutput()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (declared TEXT COLLATE case_insensitive, plain TEXT, exact TEXT COLLATE binary);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('Ada', 'ada', 'ada');");

        // Act / Assert
        (await RowsAsync(session, "SELECT declared FROM names UNION SELECT plain FROM names;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT plain FROM names UNION SELECT declared FROM names;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT declared FROM names UNION SELECT exact FROM names;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT exact FROM names UNION SELECT declared FROM names;")).Count.ShouldBe(2);
        (await RowsAsync(session, "SELECT declared COLLATE binary FROM names UNION SELECT declared FROM names;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT declared COLLATE binary FROM names UNION SELECT plain FROM names;")).Count.ShouldBe(2);
        (await RowsAsync(session, "SELECT plain FROM names WHERE plain IN (SELECT declared FROM names UNION SELECT declared FROM names);")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: system relations are captured in every operand position")]
    public async Task Execute_SystemOperands_ShouldCaptureCatalog()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT NOT NULL);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('names');");
        await ExecuteAsync(session, "CREATE TABLE target (name TEXT);");

        // Act / Assert
        (await RowsAsync(session, "SELECT name FROM names UNION SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;"))
            .ShouldContain(row => Equals(row[0], "target"));
        (await RowsAsync(session, "SELECT name FROM names WHERE name IN (SELECT name FROM names UNION SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES);"))
            .ShouldHaveSingleItem();
        (await ExecuteAsync(session, "INSERT INTO target SELECT name FROM names UNION SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;")).AffectedCount.ShouldBe(2);
        await using var result = (await ExecuteAsync(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES UNION SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns[0].IsNullable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: relational operand operators compose")]
    public async Task Execute_RelationalOperands_ShouldCompose()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "SELECT DISTINCT a.id FROM a INNER JOIN b ON a.id = b.id WHERE a.id IN (SELECT id FROM b) UNION SELECT id FROM a WHERE id > 1 GROUP BY id HAVING COUNT(*) > 1 ORDER BY 1;");

        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: both operands share captured visibility and own writes")]
    public async Task Execute_CapturedStatement_ShouldShareSnapshot()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(writer, "CREATE TABLE a (id INT);");
        await ExecuteAsync(writer, "CREATE TABLE b (id INT);");
        await ExecuteAsync(writer, "INSERT INTO a VALUES (1);");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (1);");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var captured = new SqlStatementContext(transaction, database.Coordinator);
            var request = SqlQueryRequest.FromSql("SELECT id FROM a UNION ALL SELECT id FROM b ORDER BY 1;");
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            await using var write = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(writer, "INSERT INTO a VALUES (2);");
            await ExecuteAsync(writer, "INSERT INTO b VALUES (2);");
            await write.CommitAsync(CancellationToken.None);
            await using var result = (await executor.ExecuteAsync(request, captured, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert
            (await ReadAsync(result)).Select(row => row[0]).ShouldBe(new object?[] { 1, 1 });
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }

        // Arrange / Act / Assert
        await using var own = await writer.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(writer, "INSERT INTO a VALUES (3);");
        await ExecuteAsync(writer, "INSERT INTO b VALUES (3);");
        (await RowsAsync(writer, "SELECT id FROM a WHERE id = 3 UNION ALL SELECT id FROM b WHERE id = 3;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 3, 3 });
        await own.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: derived outputs retain unified modifiers and nullability")]
    public async Task Plan_DerivedSetOutput_ShouldPublishIndependentMetadata()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE required (id INT NOT NULL, name VARCHAR(3) NOT NULL);");
        await ExecuteAsync(session, "CREATE TABLE optional (id BIGINT, name VARCHAR(5));");

        // Act
        await using var result = (await ExecuteAsync(session,
            "SELECT d.id, d.name FROM (SELECT id, name FROM required UNION SELECT id, name FROM optional) d;"))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns[0].Type.ShouldBe(DatabaseType.Int64);
        result.Columns.ShouldAllBe(column => column.IsNullable);
        result.Columns[1].MaxLength.ShouldBeNull();
        await using var unknown = (await ExecuteAsync(session,
            "SELECT NULL AS name FROM required UNION SELECT name FROM optional;")).ShouldBeAssignableTo<DatabaseResultSet>();
        unknown.Columns[0].Type.ShouldBe(DatabaseType.String);
        unknown.Columns[0].MaxLength.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Set operations: canonical vocabulary leaves stored identifier text unchanged")]
    [InlineData("union", "\"union\"")]
    [InlineData("intersect", "\"intersect\"")]
    [InlineData("except", "\"except\"")]
    [InlineData("corresponding", "corresponding")]
    public void Render_ExistingIdentifiers_ShouldKeepPersistedText(string name, string canonical)
    {
        // Arrange
        var select = new SqlQueryParser().Parse($"SELECT 1 FROM t WHERE \"{name}\" >= 0")
            .ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Act / Assert
        SqlExpressionRenderer.Render(select.Where!).ShouldBe(canonical + " >= 0");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: structural equivalence includes operands flags tails and WITH")]
    public void SameQuery_SetBodies_ShouldCompareStructure()
    {
        // Arrange
        static SqlQueryBodyExpression Parse(string sql) => new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>()
            .SqlExpression.ShouldBeAssignableTo<SqlQueryBodyExpression>();
        var first = Parse("WITH q AS (SELECT id FROM t) SELECT id FROM q UNION ALL (SELECT id FROM u EXCEPT SELECT id FROM v) ORDER BY 1 LIMIT 2");

        // Act / Assert
        SqlPersistedExpression.SameQuery(first, Parse(SqlExpressionRenderer.Render(first))).ShouldBeTrue();
        SqlPersistedExpression.SameQuery(first, Parse(SqlExpressionRenderer.Render(first).Replace("UNION ALL", "UNION"))).ShouldBeFalse();
        SqlPersistedExpression.SameQuery(first, Parse(SqlExpressionRenderer.Render(first).Replace("FROM v", "FROM w"))).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: ALL counts and tuple NULLs remain distinct from DISTINCT")]
    public async Task Execute_RepeatedTuples_ShouldCountBothInputs()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        const string prefix = "WITH l AS (SELECT 1 AS x UNION ALL SELECT 1 UNION ALL SELECT 1), " +
            "r AS (SELECT 1 AS x UNION ALL SELECT 1) ";

        // Act / Assert
        (await RowsAsync(session, prefix + "SELECT x FROM l INTERSECT ALL SELECT x FROM r;")).Count.ShouldBe(2);
        (await RowsAsync(session, prefix + "SELECT x FROM l EXCEPT ALL SELECT x FROM r;")).Count.ShouldBe(1);
        (await RowsAsync(session, prefix + "SELECT x FROM l INTERSECT SELECT x FROM r;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT NULL, id FROM a UNION SELECT NULL, id FROM b;")).Count.ShouldBe(4);
        (await RowsAsync(session, "SELECT X'0102', id FROM a INTERSECT SELECT X'0102', id FROM b;")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: identical domains survive independently synthesized outputs")]
    public async Task Plan_IdenticalDomains_ShouldRetainOnlyCommonIdentity()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE DOMAIN amount AS DECIMAL(8, 2);");
        await ExecuteAsync(session, "CREATE TABLE prices (cost amount);");
        await ExecuteAsync(session, "INSERT INTO prices VALUES (1.5);");

        // Act
        var query = SqlQueryRequest.FromSql("SELECT d.cost FROM (SELECT cost FROM prices UNION SELECT cost FROM prices) d;");
        var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(query.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var mixedQuery = SqlQueryRequest.FromSql("SELECT d.cost FROM (SELECT cost FROM prices UNION SELECT CAST(cost AS DECIMAL(8, 2)) FROM prices) d;");
        var mixed = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(mixedQuery.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Assert
        var column = plan.Bindings[0].Source.Columns[0];
        column.DomainName.ShouldBe("amount");
        column.Type.Precision.ShouldBe(8);
        column.Type.Scale.ShouldBe(2);
        mixed.Bindings[0].Source.Columns[0].DomainName.ShouldBeNull();
        var set = plan.Bindings[0].Source.Derived!.Query.ShouldBeOfType<SqlSetOperationPlan>();
        set.OutputProjections[0].ColumnOrdinal.ShouldBeNull();
        set.OutputProjections[0].Expression.ShouldBeNull();
        set.OutputProjections[0].Metadata!.SourceColumn.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Set operations: old canonical keyword definitions reapply without catalog drift")]
    public async Task ApplySchema_ExistingCanonicalVocabulary_ShouldRemainNoOp()
    {
        // Arrange
        string[] names = ["union", "intersect", "except", "corresponding"];
        string[] canonical = ["\"union\" >= 0", "\"intersect\" >= 0", "\"except\" >= 0", "corresponding >= 0"];
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "sets", false, [],
            [new CompiledSchemaTable("identifiers", "Tests.Identifiers",
                [new("id", DatabaseType.Int32, false), .. names.Select(name => new CompiledSchemaColumn(name, DatabaseType.Int32, false))],
                new CompiledSchemaKey("pk_identifiers", ["id"]), [],
                names.Select((name, i) => new CompiledSchemaConstraint("ck_" + name, CompiledSchemaConstraintKind.Check,
                    [name], null, [], new CompiledSchemaExpression(canonical[i]))).ToArray())], []);
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("sets");

        // Act
        (await database.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeFalse();
        var reapplied = await database.ApplySchemaAsync(schema);

        // Assert
        reapplied.WasAlreadyApplied.ShouldBeTrue();
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var checks = await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS ORDER BY CONSTRAINT_NAME;");
        checks.Select(row => row[0]).OrderBy(value => value).ShouldBe(canonical.Cast<object?>().OrderBy(value => value));
    }

    private static SqlDatabaseEngine CreateEngine() => SqlDatabaseEngine.Create("set-tests", new SqlDatabaseEngineOptions());

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE a (id INT);");
        await ExecuteAsync(session, "CREATE TABLE b (id INT);");
        await ExecuteAsync(session, "INSERT INTO a VALUES (NULL), (NULL), (1), (1), (2), (2);");
        await ExecuteAsync(session, "INSERT INTO b VALUES (NULL), (1), (3);");
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql)
        => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
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
