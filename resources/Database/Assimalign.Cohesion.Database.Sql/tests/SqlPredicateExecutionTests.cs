using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlPredicateExecutionTests
{
    private const string oneRow = " FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = 'dbo'";

    public static IEnumerable<object[]> DistinctCases => new object[][]
    {
        ["NULL", "NULL", false], ["NULL", "1", true], ["1", "NULL", true], ["1", "1", false], ["1", "2", true],
    };

    [Theory(DisplayName = "Cohesion Test [Sql] - Distinct: NULL-safe truth table and its complement execute")]
    [MemberData(nameof(DistinctCases))]
    public async Task Execute_DistinctTruthTable_ShouldReturnNonNullComplementAsync(string left, string right, bool expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, $"SELECT {left} IS DISTINCT FROM {right}, {left} IS NOT DISTINCT FROM {right}" + oneRow);
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, !expected });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean tests: F571 truth table never returns NULL")]
    [InlineData("TRUE", true, false, false)]
    [InlineData("FALSE", false, true, false)]
    [InlineData("NULL", false, false, true)]
    public async Task Execute_BooleanTruthTable_ShouldReturnAllSixTestsAsync(string value, bool isTrue, bool isFalse, bool isUnknown)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, $"SELECT {value} IS TRUE, {value} IS NOT TRUE, {value} IS FALSE, {value} IS NOT FALSE, {value} IS UNKNOWN, {value} IS NOT UNKNOWN" + oneRow);
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { isTrue, !isTrue, isFalse, !isFalse, isUnknown, !isUnknown });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Predicates: every PostgreSQL precedence boundary executes")]
    [InlineData("TRUE OR FALSE AND FALSE", true)]
    [InlineData("TRUE AND NOT FALSE", true)]
    [InlineData("NOT NULL IS NULL", false)]
    [InlineData("1 = 1 IS TRUE", true)]
    [InlineData("1 = NULL IS NULL", true)]
    [InlineData("TRUE = 'abc' LIKE 'a%'", true)]
    [InlineData("1 IN (1) = TRUE", true)]
    [InlineData("1 IN (1) IN (TRUE)", true)]
    [InlineData("(1 IN (1)) IN (TRUE)", true)]
    [InlineData("1 NOT IN (2) BETWEEN FALSE AND TRUE", true)]
    [InlineData("(1 NOT IN (2)) BETWEEN FALSE AND TRUE", true)]
    [InlineData("1 IN (SELECT 1" + oneRow + ") IN (TRUE)", true)]
    [InlineData("1 NOT IN (SELECT 2" + oneRow + ") BETWEEN FALSE AND TRUE", true)]
    [InlineData("1 IN (SELECT 1" + oneRow + ") NOT IN (SELECT FALSE" + oneRow + ")", true)]
    [InlineData("1 NOT IN (SELECT 2" + oneRow + ") IN (TRUE) IN (SELECT TRUE" + oneRow + ")", true)]
    [InlineData("1 IN (SELECT 1" + oneRow + ") = TRUE", true)]
    [InlineData("CAST('a' AS CHAR(3)) IS NOT DISTINCT FROM 'a'", true)]
    [InlineData("NOT EXISTS (SELECT id FROM t) IS TRUE", false)]
    [InlineData("NOT EXISTS (SELECT id FROM t) = FALSE", true)]
    [InlineData("FALSE IS DISTINCT FROM 1 = 1", true)]
    [InlineData("NULL IS NULL || '' LIKE '____'", true)]
    [InlineData("TRUE = NOT FALSE", true)]
    [InlineData("NULL IS NULL = TRUE", true)]
    [InlineData("'a%' LIKE 'a' || '!%' ESCAPE '!' || ''", true)]
    [InlineData("TRUE BETWEEN 1 = 1 AND TRUE", true)]
    [InlineData("'x' || 1 + 2", "x3")]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("-CAST('1' COLLATE binary AS INT)", -1L)]
    [InlineData("2 * -3", -6L)]
    [InlineData("'A' COLLATE case_insensitive = 'a'", true)]
    public async Task Execute_PrecedenceBoundary_ShouldMatchPostgreSqlAsync(string expression, object expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        (await RowsAsync(session, "SELECT " + expression + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE ESCAPE: raw scalar tokenization precedes all collation transforms")]
    [InlineData("binary")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    [InlineData("invariant")]
    public async Task Execute_EscapedPatterns_ShouldPreserveLiteralWildcardsAsync(string collation)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] expressions =
        [
            "'50%' LIKE '50!%' ESCAPE '!'", "'500' NOT LIKE '50!%' ESCAPE '!'",
            "'a_b' LIKE 'a!_b' ESCAPE '!'", "'a!b' LIKE 'a!!b' ESCAPE '!'",
            "'a%' LIKE 'aX%' ESCAPE 'X'", "'a_' LIKE 'aé_' ESCAPE 'é'",
            "'a%' LIKE 'á%' ESCAPE '́'", "'a%' LIKE 'a😀%' ESCAPE '😀'",
            "'%' LIKE '%%' ESCAPE '%'", "'_' LIKE '__' ESCAPE '_'",
            "'any' NOT LIKE '%%' ESCAPE '%'", "'ab' NOT LIKE '__' ESCAPE '_'",
            "'ab' NOT LIKE 'á%' ESCAPE '́'",
            "'aq' LIKE 'a!q' ESCAPE '!'", "'a%' LIKE 'a\\%'", "'a\\z' LIKE 'a\\%' ESCAPE ''",
            "'😀' LIKE '_'", "'a%' LIKE ('a' || '!%') ESCAPE ('!' || '')",
        ];
        // Act / Assert
        foreach (string expression in expressions)
        {
            (await RowsAsync(session, "SELECT " + (expression.Contains(" NOT LIKE ", StringComparison.Ordinal) ? expression.Replace(" NOT LIKE ", " COLLATE " + collation + " NOT LIKE ") : expression.Replace(" LIKE ", " COLLATE " + collation + " LIKE ")) + oneRow))
                .ShouldHaveSingleItem()[0].ShouldBe(true, expression + " / " + collation);
        }
        (await RowsAsync(session, "SELECT 'x' LIKE 'x' ESCAPE NULL" + oneRow)).ShouldHaveSingleItem()[0].ShouldBeNull();
        (await RowsAsync(session, "SELECT CAST('a' AS CHAR(2)) LIKE 'a! ' ESCAPE '!'" + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE ESCAPE: an escape operand's collation participates in matching")]
    [InlineData("'A%' LIKE 'aX%' ESCAPE 'X' COLLATE case_insensitive")]
    [InlineData("'É%' LIKE 'eX%' ESCAPE 'X' COLLATE case_accent_insensitive")]
    [InlineData("'A%' LIKE 'aX%' ESCAPE (SELECT 'X' COLLATE case_insensitive FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = 'dbo')")]
    public async Task Execute_EscapeCollation_ShouldResolveWithoutNormalizingEscapeAsync(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "SELECT " + expression + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE ESCAPE: constant concatenation caches preserve SQL text formatting")]
    [InlineData("(TRUE || 'tail') LIKE TRUE || '%' ESCAPE ''")]
    [InlineData("'2024-01-01tail' LIKE DATE '2024-01-01' || '%' ESCAPE ''")]
    [InlineData("(X'01' || 'tail') LIKE X'01' || '%' ESCAPE ''")]
    public async Task Execute_CachedConcatenation_ShouldUseSqlFormattingAsync(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "SELECT " + expression + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE ESCAPE: invalid constants fail before rows and preserve the session")]
    [InlineData("'x' LIKE 'x' ESCAPE 'ab'")]
    [InlineData("'x' LIKE 'x!' ESCAPE '!'")]
    [InlineData("'x' LIKE 'x\\'")]
    public async Task Execute_InvalidConstantEscape_ShouldFailOnEmptyInputAsync(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE empty_rows (id INT)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT " + expression + " FROM empty_rows"));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS02034");
        diagnostic.Start.ShouldBe(8 + expression.IndexOf(" LIKE ", StringComparison.Ordinal));
        error.Message.ShouldContain("ESCAPE", Case.Sensitive);
        (await RowsAsync(session, "SELECT 1" + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean tests: non-Boolean operands fail during planning on empty tables")]
    [InlineData("id IS TRUE")]
    [InlineData("1 IS NOT FALSE")]
    [InlineData("'x' IS UNKNOWN")]
    [InlineData("@p IS TRUE")]
    public async Task Execute_NonBooleanTest_ShouldFailBeforeReadingRowsAsync(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE empty_rows (id INT)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT " + expression + " FROM empty_rows",
            new Dictionary<string, object?> { ["p"] = 1 }, CancellationToken.None));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS02003");
        diagnostic.Start.ShouldBe(8 + expression.IndexOf(" IS ", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Distinct: comparer identity includes NaN, collation and incompatible-type failures")]
    public async Task Execute_DistinctComparands_ShouldUseExistingComparerAsync()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "SELECT 'a' COLLATE case_insensitive IS NOT DISTINCT FROM 'A'" + oneRow)).ShouldHaveSingleItem()[0].ShouldBe(true);
        await using var nan = (await session.ExecuteAsync("SELECT @n IS NOT DISTINCT FROM @n" + oneRow, new Dictionary<string, object?> { ["n"] = double.NaN }, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in nan.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(true); }
        var equality = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT 1 = TRUE" + oneRow));
        var distinct = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT 1 IS DISTINCT FROM TRUE" + oneRow));
        distinct.Message.ShouldBe(equality.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Predicates: grouping, joins, HAVING, CASE, ordering and correlation visit all operands")]
    public async Task Execute_PredicatePositions_ShouldBindEveryWalkerAsync()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        (await RowsAsync(session, "SELECT flag IS TRUE, flag IS NULL, COUNT(*) FROM t GROUP BY flag IS TRUE, flag IS NULL ORDER BY 1, 2"))
            .ShouldBe(new object?[][] { [false, false, 1L], [false, true, 1L], [true, false, 1L] });
        (await RowsAsync(session, "SELECT flag IS TRUE, flag IS FALSE, COUNT(*) FROM t GROUP BY flag IS TRUE, flag IS FALSE ORDER BY 1, 2"))
            .ShouldBe(new object?[][] { [false, false, 1L], [false, true, 1L], [true, false, 1L] });
        (await RowsAsync(session, "SELECT flag IS DISTINCT FROM TRUE, flag IS NOT DISTINCT FROM TRUE, COUNT(*) FROM t GROUP BY flag IS DISTINCT FROM TRUE, flag IS NOT DISTINCT FROM TRUE ORDER BY 1"))
            .ShouldBe(new object?[][] { [false, true, 1L], [true, false, 2L] });
        (await RowsAsync(session, "SELECT name LIKE pattern ESCAPE '!', name LIKE pattern ESCAPE '#', COUNT(*) FROM t GROUP BY name LIKE pattern ESCAPE '!', name LIKE pattern ESCAPE '#' ORDER BY 1, 2"))
            .ShouldBe(new object?[][] { [true, false, 1L], [true, true, 2L] });
        (await RowsAsync(session, "SELECT COUNT(*) FROM t l JOIN t r ON l.flag IS NOT DISTINCT FROM r.flag")).ShouldHaveSingleItem()[0].ShouldBe(3L);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t l JOIN t r ON (l.flag = r.flag) IS TRUE")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        foreach (string predicate in new[] { "flag IS TRUE", "flag IS NOT DISTINCT FROM TRUE" })
        {
            (await RowsAsync(session, $"SELECT id FROM t WHERE {predicate}")).ShouldHaveSingleItem()[0].ShouldBe(1);
            (await RowsAsync(session, $"SELECT {predicate}, COUNT(*) FROM t GROUP BY {predicate} HAVING {predicate} ORDER BY {predicate}"))
                .ShouldHaveSingleItem().ShouldBe(new object?[] { true, 1L });
            (await RowsAsync(session, $"SELECT CASE WHEN {predicate} THEN 1 ELSE 0 END FROM t ORDER BY id"))
                .ShouldBe(new object?[][] { [1], [0], [0] });
        }
        (await RowsAsync(session, "SELECT id FROM t o WHERE (SELECT i.flag FROM t i WHERE i.id = o.id) IS TRUE")).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM t o WHERE o.flag IS NOT DISTINCT FROM (SELECT i.flag FROM t i WHERE i.id = o.id) ORDER BY id")).Count.ShouldBe(3);
        (await RowsAsync(session, "SELECT id FROM t o WHERE name LIKE 'a!%' ESCAPE (SELECT i.esc FROM t i WHERE i.id = o.id)")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT name LIKE pattern ESCAPE esc FROM t ORDER BY id")).ShouldBe(new object?[][] { [true], [true], [true] });
        (await RowsAsync(session, "SELECT MAX(id) IS DISTINCT FROM 3 FROM t")).ShouldHaveSingleItem()[0].ShouldBe(false);
        (await RowsAsync(session, "WITH c AS (SELECT 2 AS x) SELECT id FROM t WHERE id IS DISTINCT FROM (SELECT x FROM c) ORDER BY id"))
            .ShouldBe(new object?[][] { [1], [3] });
        var plan = await RowsAsync(session, "EXPLAIN SELECT id FROM t WHERE flag IS TRUE AND flag IS DISTINCT FROM NULL AND name LIKE 'a!%' ESCAPE '!'");
        plan.Single(row => Equals(row[2], "Select"))[5].ShouldBeOfType<string>()
            .ShouldBe("filter=True; order_keys=0; order_by=[]; distinct=False; limit=; offset=");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Predicate walkers: hidden columns, calls, stars and aggregate levels are validated")]
    [InlineData("SELECT -'1' COLLATE binary FROM t", "CS02003")]
    [InlineData("SELECT absent IS TRUE FROM t", "CS01015")]
    [InlineData("SELECT flag IS DISTINCT FROM absent FROM t", "CS01015")]
    [InlineData("SELECT name LIKE pattern ESCAPE absent FROM t", "CS01015")]
    [InlineData("SELECT name LIKE pattern ESCAPE NO_SUCH_FUNCTION() FROM t", "CS01016")]
    [InlineData("SELECT * IS TRUE FROM t", "CS05013")]
    [InlineData("SELECT 1 IS DISTINCT FROM * FROM t", "CS05013")]
    [InlineData("SELECT name LIKE pattern ESCAPE * FROM t", "CS05013")]
    [InlineData("SELECT COUNT(*) FROM t WHERE COUNT(*) IS DISTINCT FROM 0", "CS05017")]
    [InlineData("SELECT COUNT(*) FROM t WHERE (COUNT(*) = 1) IS TRUE", "CS05017")]
    public async Task Execute_InvalidNestedOperand_ShouldFailAdmissionAsync(string sql, string code)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Predicates: CHECK, DEFAULT, function and view text persist and reopen")]
    public async Task Execute_StoredPredicates_ShouldEnforceAfterReopenAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("predicates", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE plain (flag BOOLEAN CHECK (flag))");
            await ExecuteAsync(session, "CREATE TABLE defaults (a BOOLEAN DEFAULT 1 = 1, b BOOLEAN DEFAULT 1 IS DISTINCT FROM 2 NOT NULL)");
            await ExecuteAsync(session, "INSERT INTO defaults DEFAULT VALUES");
            await ExecuteAsync(session, "INSERT INTO plain VALUES (NULL)");
            await ExecuteAsync(session, "CREATE TABLE checks (id INT, flag BOOLEAN DEFAULT (NULL IS UNKNOWN), name TEXT, a INT, b INT, CONSTRAINT ck_truth CHECK (flag IS TRUE), CONSTRAINT ck_like CHECK (name LIKE 'a!%' ESCAPE '!'), CONSTRAINT ck_distinct CHECK (a IS NOT DISTINCT FROM b))");
            await ExecuteAsync(session, "INSERT INTO checks (id, name, a, b) VALUES (1, 'a%', NULL, NULL)");
            await ExecuteAsync(session, "CREATE FUNCTION truth(p BOOLEAN) RETURNS BOOLEAN IMMUTABLE RETURN p IS TRUE");
            await ExecuteAsync(session, "CREATE VIEW predicates AS SELECT id, truth(flag) AS truth, a IS NOT DISTINCT FROM b AS same, name LIKE 'a!%' ESCAPE '!' AS matched FROM checks");
            var texts = await RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_NAME IN ('ck_truth', 'ck_like', 'ck_distinct') ORDER BY CONSTRAINT_NAME");
            texts.Select(row => row[0]).ShouldBe(new object?[] { "a IS NOT DISTINCT FROM b", "name LIKE 'a!%' ESCAPE '!'", "flag IS TRUE" });
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("predicates", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        // Assert
        (await RowsAsync(reader, "SELECT * FROM defaults")).ShouldHaveSingleItem().ShouldBe(new object?[] { true, true });
        (await RowsAsync(reader, "SELECT * FROM predicates")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, true, true, true });
        foreach (string values in new[] { "(2, NULL, 'a%', 1, 1)", "(2, TRUE, 'abc', 1, 1)", "(2, TRUE, 'a%', NULL, 1)" })
        { await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(reader, "INSERT INTO checks VALUES " + values)); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Predicates: format 24 is refused before newly storable syntax is loaded")]
    public async Task Open_PreviousFormat_ShouldRefuseNewStoredPredicatesAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("predicates-format", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE t (flag BOOLEAN CHECK (flag IS TRUE))");
            await database.Catalog.SetRecordSpaceFormatVersionAsync(24, CancellationToken.None);
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("predicates-format", new() { StorageStrategy = storage.CaptureDurableImages() });
        var error = await Should.ThrowAsync<SqlDataStorageFormatException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));
        // Assert
        error.Message.ShouldContain("uses data-storage format 24", Case.Sensitive);
        error.Message.ShouldContain("supports only format 31", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted predicates: equality distinguishes truth, negation and ESCAPE")]
    [InlineData("flag IS TRUE", "flag IS FALSE")]
    [InlineData("flag IS TRUE", "flag IS NOT TRUE")]
    [InlineData("a IS DISTINCT FROM b", "a IS NOT DISTINCT FROM b")]
    [InlineData("name LIKE pattern", "name LIKE pattern ESCAPE '!'")]
    [InlineData("name LIKE pattern ESCAPE '!'", "name LIKE pattern ESCAPE '#'")]
    public void Canonicalize_PredicateShape_ShouldRetainAndDistinguishOperands(string text, string other)
    {
        // Arrange
        var expression = SqlPersistedExpression.Load(text, "test");
        // Act
        string canonical = SqlPersistedExpression.Canonicalize(expression, "test");
        // Assert
        canonical.ShouldBe(text);
        SqlPersistedExpression.AreEquivalent(expression, SqlPersistedExpression.Load(canonical, "test")).ShouldBeTrue();
        SqlPersistedExpression.AreEquivalent(expression, SqlPersistedExpression.Load(other, "test")).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: long backtracking observes cancellation without recursion")]
    [InlineData("binary")]
    public void Match_LongPattern_ShouldObserveCancellation(string name)
    {
        // Arrange
        var pattern = SqlLikePattern.Compile("%" + new string('a', 100_000) + "b", "!", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        // Act / Assert
        Should.Throw<OperationCanceledException>(() => pattern.Match(new string('a', 1_000_000), Collation.FromName(name), cancellation.Token));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Predicates: nonassociative chains refuse execution")]
    [InlineData("a < b < c")]
    [InlineData("a = b = c")]
    [InlineData("a IS DISTINCT FROM b IS NULL")]
    [InlineData("a LIKE b LIKE c")]
    [InlineData("a BETWEEN 1 AND 2 LIKE c")]
    public async Task Execute_NonassociativeChain_ShouldRefuseAsync(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT " + expression + oneRow + ";"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: functions used only by predicate operands retain dependencies")]
    [InlineData("'a%' LIKE 'a!%' ESCAPE escape_only()", "escape_only", "TEXT", "'!'")]
    [InlineData("1 IS DISTINCT FROM distinct_only()", "distinct_only", "INT", "2")]
    public async Task Execute_PredicateOnlyFunction_ShouldRefuseDropAsync(string expression, string name, string type, string body)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE FUNCTION {name}() RETURNS {type} IMMUTABLE RETURN {body}");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + expression + " AS matched" + oneRow);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION " + name + "()"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        (await RowsAsync(session, "SELECT matched FROM v")).ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: wildcards consume original Unicode scalars in checks, scans and seeks")]
    [InlineData("%가", true)]
    [InlineData("%각", true)]
    [InlineData("%é", false)]
    [InlineData("%ß", true)]
    [InlineData("%́", true)]
    public async Task Execute_OriginalScalarWildcard_ShouldPreserveInputBoundariesAsync(string value, bool expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE candidates (id INT, s TEXT COLLATE case_accent_insensitive)");
        await ExecuteAsync(session, "CREATE INDEX by_id ON candidates (id)");
        await ExecuteAsync(session, "CREATE TABLE checked (s TEXT COLLATE case_accent_insensitive CHECK (s LIKE '!%_' ESCAPE '!'))");
        await ExecuteAsync(session, $"INSERT INTO candidates VALUES (1, '{value}')");
        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM candidates WHERE s LIKE '!%_' ESCAPE '!'")).Count.ShouldBe(expected ? 1 : 0);
        (await RowsAsync(session, "SELECT id FROM candidates WHERE id = 1 AND s LIKE '!%_' ESCAPE '!'")).Count.ShouldBe(expected ? 1 : 0);
        (await RowsAsync(session, "EXPLAIN SELECT id FROM candidates WHERE id = 1 AND s LIKE '!%_' ESCAPE '!'")).ShouldContain(row => Equals(row[2], "IndexSeek"));
        if (expected) { await ExecuteAsync(session, $"INSERT INTO checked VALUES ('{value}')"); }
        else { await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, $"INSERT INTO checked VALUES ('{value}')")); }
        (await RowsAsync(session, "SELECT s FROM checked")).Count.ShouldBe(expected ? 1 : 0);
        (await RowsAsync(session, "SELECT '각' COLLATE case_accent_insensitive LIKE '_', '각' COLLATE case_accent_insensitive LIKE '각', 'éb' COLLATE case_accent_insensitive LIKE 'é_', '́' COLLATE case_accent_insensitive LIKE '́'" + oneRow))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { true, true, true, true });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: repetitive nonmatching collation paths cancel within a bound")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    public void Match_RepeatedWildcardFailure_ShouldCancelCleanly(string name)
    {
        // Arrange
        // A long final literal still requires backtracking after the fast path removes
        // repeated suffix exploration; the separate resource test must finish uncancelled.
        var pattern = SqlLikePattern.Compile(string.Concat(Enumerable.Repeat("%a", 128)) + new string('a', 100_000) + "b", "!", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        // Act / Assert
        Should.Throw<OperationCanceledException>(() => pattern.Match(new string('a', 200_000), Collation.FromName(name), cancellation.Token));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: a pathological invariant pattern stops at a polynomial work budget")]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    public void Match_LinguisticFailure_ShouldRefuseWithinWorkBudget(int length)
    {
        // Arrange
        const int wildcards = 32;
        var pattern = SqlLikePattern.Compile(string.Concat(Enumerable.Repeat("%เก", wildcards)) + "ข", "!", CancellationToken.None);
        string input = string.Concat(Enumerable.Repeat("เก", length / 2));
        long work = 0;
        // Act
        var error = Should.Throw<DatabaseException>(() => pattern.Match(input, Collation.Invariant, CancellationToken.None, out work));
        // Assert: comparison lengths as well as transitions consume the same finite budget.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05040");
        work.ShouldBeGreaterThan(0);
        work.ShouldBeLessThanOrEqualTo(pattern.WorkLimit(length));
        pattern.WorkLimit(length).ShouldBe(64L * (length + 1) * (3 * wildcards + 2));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: repetitive failures have deterministic search and allocation bounds")]
    [InlineData("binary", 200_000, 128, 64 * 1024)]
    [InlineData("case_insensitive", 200_000, 128, 24 * 1024 * 1024)]
    [InlineData("case_accent_insensitive", 200_000, 128, 64 * 1024 * 1024)]
    public void Match_RepeatedWildcardFailure_ShouldCompleteWithinResourceBounds(string name, int length, int wildcards, long allocationLimit)
    {
        // Arrange
        var collation = Collation.FromName(name);
        var pattern = SqlLikePattern.Compile(string.Concat(Enumerable.Repeat("%a", wildcards)) + "b", "!", CancellationToken.None).Normalize(collation);
        string input = new('a', length);
        pattern.Match("aa", collation, CancellationToken.None).ShouldBeFalse();
        long allocated = long.MaxValue, work = 0;
        // Act: the minimum of three rounds excludes one-time runtime initialization.
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool matched = pattern.Match(input, collation, CancellationToken.None, out work);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            matched.ShouldBeFalse();
        }
        // Assert
        work.ShouldBeLessThanOrEqualTo(16L * (length + 1) * (wildcards + 1));
        allocated.ShouldBeLessThan(allocationLimit);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: thousands of wildcards retain bounded work and allocation (#1394)")]
    [InlineData("binary", 0)]
    [InlineData("case_insensitive", 1024 * 1024)]
    [InlineData("case_accent_insensitive", 8 * 1024 * 1024)]
    [InlineData("invariant", 4 * 1024 * 1024)]
    public void Match_ThousandsOfWildcards_ShouldBoundWorkAndAllocation(string name, long allocationLimit)
    {
        // Arrange: tokenization and fixed-pattern normalization stay outside the match.
        const int length = 8192, wildcards = 2048;
        var collation = Collation.FromName(name);
        var pattern = SqlLikePattern.Compile(string.Concat(Enumerable.Repeat("%a", wildcards)) + "b", "!", CancellationToken.None).Normalize(collation);
        string input = new('a', length);
        pattern.Match("aa", collation, CancellationToken.None).ShouldBeFalse();
        long allocated = long.MaxValue, work = 0;
        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool matched = false;
            string? code = null;
            try { matched = pattern.Match(input, collation, CancellationToken.None, out work); }
            catch (SqlPlanningException exception) { code = exception.Diagnostics[0].Code; }
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            matched.ShouldBeFalse();
            code.ShouldBeNull();
        }
        // Assert: ordinal matching allocates nothing; normalization and the visited-bit
        // matrix have size budgets, independent of the number of backtracking steps.
        work.ShouldBeLessThanOrEqualTo(collation.IsIndexBacked
            ? 16L * (length + 1) * (wildcards + 1) : pattern.WorkLimit(length));
        allocated.ShouldBeLessThanOrEqualTo(allocationLimit);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: ignored scalar runs have linear literal boundary work (#1394)")]
    [InlineData("%a", true)]
    [InlineData("%a", false)]
    [InlineData("%́%c", true)]
    [InlineData("%́%c", false)]
    public void Match_IgnoredScalarRun_ShouldCoalesceLiteralBoundaries(string text, bool leading)
    {
        // Arrange
        const int length = 200_000;
        string marks = new('́', length);
        string input = leading ? marks + "ab" : "a" + marks + "b";
        var pattern = SqlLikePattern.Compile(text, "!", CancellationToken.None).Normalize(Collation.CaseAccentInsensitive);
        // Act
        bool matched = pattern.Match(input, Collation.CaseAccentInsensitive, CancellationToken.None, out long work);
        // Assert
        matched.ShouldBeFalse();
        work.ShouldBeLessThanOrEqualTo(16L * (input.Length + 1) * (text.Length + 1));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - LIKE: coalesced accent boundaries agree with an exhaustive scalar oracle (#1394)")]
    public void Match_AccentBoundaries_ShouldRetainAllLiteralEnds()
    {
        // Arrange: short BMP scalars keep this deliberately exhaustive oracle small.
        string?[][] patterns = [[null, "a"], [null, "́", null, "b"], [null, "a", ""], ["́", null, "a"], [null, "ß", ""], [null, "가"], [null, "", "́", null, "b"]];
        string[] scalars = ["a", "b", "́", "ß", "가", "ᄀ", "ᅡ"];
        var inputs = new List<string> { "" };
        for (int depth = 0, first = 0; depth < 3; depth++)
        {
            int last = inputs.Count;
            for (int index = first; index < last; index++)
            { foreach (string scalar in scalars) { inputs.Add(inputs[index] + scalar); } }
            first = last;
        }
        int comparisons = 0;
        // Act / Assert
        foreach (var tokens in patterns)
        {
            string text = string.Concat(tokens.Select(token => token is null ? "%" : token.Length == 0 ? "_" : token));
            var compiled = SqlLikePattern.Compile(text, "!", CancellationToken.None).Normalize(Collation.CaseAccentInsensitive);
            foreach (string input in inputs)
            {
                compiled.Match(input, Collation.CaseAccentInsensitive, CancellationToken.None).ShouldBe(MatchSlow(0, 0), text + ": " + input);
                comparisons++;

                bool MatchSlow(int token, int start)
                {
                    if (token == tokens.Length) { return start == input.Length; }
                    if (tokens[token] is "") { return start < input.Length && MatchSlow(token + 1, start + 1); }
                    for (int end = start; end <= input.Length; end++)
                    {
                        if ((tokens[token] is null || Collation.CaseAccentInsensitive.Compare(input[start..end], tokens[token]!) == 0) && MatchSlow(token + 1, end)) { return true; }
                    }
                    return false;
                }
            }
        }
        comparisons.ShouldBe(2800);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: ordinary invariant patterns succeed through 200000 characters")]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(200000)]
    public async Task Execute_CommonInvariantPatterns_ShouldSucceedAtOrdinarySizesAsync(int length)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string a = $"RPAD('a', {length}, 'a')";
        string middle = $"RPAD('z', {length - 3}, 'z') || 'abc'";
        string email = $"RPAD('a', {length - 12}, 'a') || '@example.com'";
        string prefix = $"'abc' || RPAD('z', {length - 3}, 'z')";
        string[] expressions =
        [
            $"{a} COLLATE invariant LIKE '%a'",
            $"({middle}) COLLATE invariant LIKE '%abc%'",
            $"({email}) COLLATE invariant LIKE '%@example.com'",
            $"({prefix}) COLLATE invariant LIKE 'abc%'",
            $"{a} COLLATE invariant LIKE 'abc%'",
            $"{a} COLLATE invariant NOT LIKE '%b'",
            $"{a} COLLATE invariant NOT LIKE '%a'",
            $"{a} COLLATE invariant LIKE '%abc%'",
        ];
        object?[] expected = [true, true, true, true, false, true, false, false];
        string projections = string.Join(", ", expressions.Select((expression, index) => expression + " AS c" + index));
        string checkedValue = length <= 1024 ? "s" : $"RPAD(s, {length}, 'x')";
        await ExecuteAsync(session, $"CREATE TABLE checked_common (s TEXT CHECK ({checkedValue} COLLATE invariant LIKE '%x%'))");
        await ExecuteAsync(session, "CREATE VIEW common_view AS SELECT " + projections + oneRow);
        // Act / Assert: direct expressions, cached view expressions and a stored CHECK.
        (await RowsAsync(session, "SELECT " + projections + oneRow)).ShouldHaveSingleItem().ShouldBe(expected);
        (await RowsAsync(session, "SELECT * FROM common_view")).ShouldHaveSingleItem().ShouldBe(expected);
        // Physical rows have an 8092-byte limit. Small values use the original
        // column CHECK; large CHECK inputs are constructed from a stored seed.
        string storedValue = length <= 1024 ? $"RPAD('x', {length}, 'x')" : "'x'";
        await ExecuteAsync(session, $"INSERT INTO checked_common VALUES ({storedValue})");
        (await RowsAsync(session, $"SELECT LENGTH({checkedValue}) FROM checked_common")).ShouldHaveSingleItem()[0].ShouldBe((long)length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: ordinary invariant searches have linear work")]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(200000)]
    public void Match_CommonInvariantPatterns_ShouldUseLinearWork(int length)
    {
        // Arrange
        (string Text, string Input, bool Expected)[] cases =
        [
            ("%a", new string('a', length), true),
            ("%abc%", new string('z', length - 3) + "abc", true),
            ("%@example.com", new string('a', length - 12) + "@example.com", true),
            ("abc%", "abc" + new string('a', length - 3), true),
            ("abc%", new string('z', length), false),
            ("%abc%", new string('a', length), false),
            ("%b", new string('a', length), false),
            ("%a_bc", new string('a', length - 2) + "bc", true),
            ("%a", new string('é', length - 1) + "a", true),
            ("%abc%", new string('中', length - 3) + "abc", true),
            ("%@example.com", new string('é', length - 12) + "@example.com", true),
            ("abc%", "abc" + new string('é', length - 3), true),
            ("abc%", new string('中', length), false),
            ("%a", new string('\0', length - 1) + "a", true),
            ("%a", "a" + new string('\u00ad', length - 1), true),
            ("%a", new string('́', length - 1) + "a", true),
            ("%a", new string('́', length), false),
            ("%a", new string('a', length - 1) + "́", false),
            ("%a", "a" + new string('\u034f', length - 1), true),
            ("%@example.com", new string('́', length - 12) + "@example.com", true),
            ("%é", new string('x', length - 2) + "é", true),
            ("%abc%", new string('́', length - 3) + "abc", true),
            ("%abc%", new string('́', length), false),
            ("%abc%", "abc" + new string('́', length - 3), true),
            ("abc%", new string('́', length), false),
            ("abc%", "abc" + new string('́', length - 3), true),
            ("abc%", new string('\u034f', length - 3) + "abc", true),
            ("a%b", "a" + new string('́', length - 2) + "b", true),
            ("a%b", "z" + new string('́', length - 2) + "b", false),
            ("a%b", "a" + new string('\u034f', length - 2) + "c", false),
        ];
        // Act / Assert
        foreach (var (text, input, expected) in cases)
        {
            var pattern = SqlLikePattern.Compile(text, "!", CancellationToken.None);
            pattern.Match(input, Collation.Invariant, CancellationToken.None, out long work).ShouldBe(expected);
            work.ShouldBeLessThanOrEqualTo((text == "%a_bc" || input.Contains('́') || input.Contains('\u034f') ? 16L : 4L) * (length + text.Length + 1));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: invariant anchors preserve ignored scalar ends")]
    [InlineData("a\0", "%a", true)]
    [InlineData("\0a\0", "%a", true)]
    [InlineData("\0a\0", "%a_", true)]
    [InlineData("a\0b", "a_b", true)]
    [InlineData("\0a", "\0_a", true)]
    [InlineData("", "%\0", true)]
    [InlineData("a\u0001b", "a_", true)]
    [InlineData("a\u0640b", "a_", true)]
    [InlineData("a\u07fab", "a_", true)]
    [InlineData("a\u0824b", "a%", true)]
    [InlineData("a\u0828b", "a_b", true)]
    public void Match_InvariantAnchors_ShouldPreserveIgnorableScalars(string input, string text, bool expected)
    {
        // Arrange
        var pattern = SqlLikePattern.Compile(text, "!", CancellationToken.None);
        // Act / Assert
        pattern.Match(input, Collation.Invariant, CancellationToken.None).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - LIKE: a long invariant terminal literal compares its complete suffix once (#1394)")]
    public void Match_LongLinguisticLiteral_ShouldStayWithinLinearWork()
    {
        // Arrange
        string input = new('a', 200_000);
        var pattern = SqlLikePattern.Compile(input, "!", CancellationToken.None);
        // Act
        bool matched = pattern.Match(input, Collation.Invariant, CancellationToken.None, out long work);
        // Assert
        matched.ShouldBeTrue();
        work.ShouldBeLessThanOrEqualTo(3L * input.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: plans retain cancellation only when they contain LIKE")]
    [InlineData("SELECT s COLLATE invariant LIKE '%b' FROM slow", true)]
    [InlineData("SELECT s FROM slow WHERE s COLLATE invariant LIKE '%b'", true)]
    [InlineData("SELECT CASE WHEN s LIKE '%b' THEN 1 ELSE 0 END FROM slow", true)]
    [InlineData("WITH x AS (SELECT s LIKE '%b' AS matched FROM slow) SELECT matched FROM x", true)]
    [InlineData("SELECT s FROM slow WHERE EXISTS (SELECT s FROM slow child WHERE child.s LIKE '%b')", true)]
    [InlineData("SELECT s FROM slow", false)]
    public async Task Plan_LikeExpressions_ShouldRetainCancellationFlagAsync(string sql, bool expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE slow (s TEXT)");
        // Act
        var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        // Assert
        plan.ContainsLike.ShouldBe(expected);
        database.Definitions.Functions.RunsApplicationCode.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: cancellation at row-time match entry uses the current statement token")]
    [InlineData("SELECT matched FROM slow_view", true)]
    [InlineData("INSERT INTO checked_values VALUES (2, 'aaa')", false)]
    [InlineData("UPDATE checked_values SET s = 'aaa' WHERE id = 1", false)]
    public async Task ExecuteAsync_OneRowLike_ShouldObserveStatementCancellationAsync(string sql, bool view)
    {
        // Arrange: cached patterns are bound with no cancellation token. An evaluator
        // hook cancels at Match entry, after operand evaluation, without
        // registering application code or racing a faster work-budget refusal.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string literal = new string('a', 1000) + "b";
        await ExecuteAsync(session, "CREATE TABLE slow (s TEXT)");
        await ExecuteAsync(session, "INSERT INTO slow VALUES ('a')");
        await ExecuteAsync(session, "CREATE VIEW slow_view AS SELECT RPAD('a', 500000, 'a') COLLATE invariant LIKE '%" + literal + "' AS matched FROM slow");
        await ExecuteAsync(session, "CREATE TABLE checked_values (id INT, s TEXT CHECK (s COLLATE invariant LIKE '%" + literal + "'))");
        await ExecuteAsync(session, "INSERT INTO checked_values VALUES (1, '" + literal + "')");
        SqlBoundLike like;
        if (view)
        {
            var outer = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
            var child = outer.Bindings[0].Source.Derived!.Query.ShouldBeOfType<SqlSelectPlan>();
            like = child.Projections[0].Value.ShouldBeOfType<SqlBoundLike>();
        }
        else
        {
            database.Catalog.TryGetTable("dbo", "checked_values", out var table).ShouldBeTrue();
            like = database.Definitions.Get(table!).Checks[0].Bound.ShouldBeOfType<SqlBoundLike>();
        }
        like.Compiled.ShouldNotBeNull();
        using var cancellation = new CancellationTokenSource();
        int entered = 0;
        SqlExpressionEvaluator.BeforeLikeMatch = () => { entered++; cancellation.Cancel(); };
        database.Definitions.Functions.RunsApplicationCode.ShouldBeFalse();
        // Act
        try
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
        }
        finally { SqlExpressionEvaluator.BeforeLikeMatch = null; }
        // Assert: the matcher was reached, cancellation escaped, and writes rolled back.
        entered.ShouldBe(1);
        if (view) { session.LastStatementMetrics!.RecordsExamined.ShouldBe(1); }
        (await RowsAsync(session, "SELECT id FROM checked_values")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: invariant literals probe past reordered Thai and Lao prefixes")]
    [InlineData("เก")]
    [InlineData("ເກ")]
    public async Task Execute_ReorderedLinguisticLiteral_ShouldMatchIdenticalTextAsync(string text)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, $"SELECT '{text}' COLLATE invariant LIKE '{text}', '{text}' COLLATE invariant LIKE '{text}%'" + oneRow);
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { true, true });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: simple case folding preserves supplementary scalar wildcard boundaries")]
    [InlineData("𐐀A", "𐐨_")]
    [InlineData("𐐀A", "_a")]
    [InlineData("𐐀A", "%𐐨a")]
    public void Match_SimpleCaseFold_ShouldPreserveScalarBoundaries(string input, string text)
    {
        // Arrange
        var pattern = SqlLikePattern.Compile(text, "!", CancellationToken.None);
        // Act
        bool matched = pattern.Match(input, Collation.CaseInsensitive, CancellationToken.None);
        // Assert
        matched.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Predicates: runtime operand failures retain their source location")]
    [InlineData("distinct", "CS02003")]
    [InlineData("boolean", "CS02003")]
    [InlineData("escape", "CS02034")]
    public void Evaluate_InvalidRuntimeOperand_ShouldRetainLocation(string kind, string code)
    {
        // Arrange
        var location = Location.Create(1, 1, 9, 13);
        var collation = SqlBoundCollation.Of(default, default, Collation.Binary);
        SqlBoundExpression expression = kind switch
        {
            "distinct" => new SqlBoundDistinct(new SqlBoundConstant(new PredicateFaultComparand()), new SqlBoundConstant(new PredicateFaultComparand()), false, collation, location),
            "boolean" => new SqlBoundBooleanTest(new SqlBoundConstant(1), SqlTruthValue.True, false, location),
            _ => new SqlBoundLike(new SqlBoundConstant("x"), new SqlBoundConstant("x!"), new SqlBoundConstant("!"), false, collation, null, location),
        };
        var evaluator = new SqlExpressionEvaluator([], null);
        // Act
        var error = Should.Throw<DatabaseException>(() => evaluator.Evaluate(expression, []));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(9);
        diagnostic.End.ShouldBe(13);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - LIKE: every collation advances wildcards over raw scalars")]
    [InlineData("binary")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    [InlineData("invariant")]
    public void Match_OriginalScalars_ShouldCountBeforeCollation(string name)
    {
        // Arrange
        var collation = Collation.FromName(name);
        var pattern = SqlLikePattern.Compile("!%_", "!", CancellationToken.None);
        // Act / Assert
        pattern.Match("%가", collation, CancellationToken.None).ShouldBeTrue();
        pattern.Match("%é", collation, CancellationToken.None).ShouldBeFalse();
        pattern.Match("%ß", collation, CancellationToken.None).ShouldBeTrue();
        pattern.Match("%́", collation, CancellationToken.None).ShouldBeTrue();
        SqlLikePattern.Compile("a_b", "!", CancellationToken.None).Match("áb", collation, CancellationToken.None).ShouldBeTrue();
        SqlLikePattern.Compile("é_", "!", CancellationToken.None).Match("éb", Collation.Invariant, CancellationToken.None).ShouldBeTrue();
        SqlLikePattern.Compile("가_", "!", CancellationToken.None).Match("가b", Collation.Invariant, CancellationToken.None).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - LIKE: the binary fast path retains scalar validation")]
    public void Match_InvalidUtf16_ShouldRetainBinaryValidation()
    {
        // Arrange
        var wildcard = SqlLikePattern.Compile("_", "!", CancellationToken.None);
        var literal = SqlLikePattern.Compile("\ud800", "!", CancellationToken.None);
        // Act / Assert
        Should.Throw<DatabaseTypeException>(() => wildcard.Match("\ud800", Collation.Binary, CancellationToken.None));
        Should.Throw<DatabaseTypeException>(() => literal.Match("x", Collation.Binary, CancellationToken.None));
    }

    internal static SqlDatabaseEngine CreateEngine() => SqlDatabaseEngine.Create("predicates", new());

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id INT, flag BOOLEAN, name TEXT, pattern TEXT, esc TEXT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, TRUE, 'ann', 'a%', '!'), (2, NULL, 'a%b', 'a!%b', '!'), (3, FALSE, 'bob', 'b%', '!')");
    }

    internal static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql)
        => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    internal static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
