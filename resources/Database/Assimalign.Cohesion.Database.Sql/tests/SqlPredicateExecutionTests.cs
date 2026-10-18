using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
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
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE034");
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
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE003");
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
        (await RowsAsync(session, "EXPLAIN SELECT id FROM t WHERE flag IS TRUE AND flag IS DISTINCT FROM NULL AND name LIKE 'a!%' ESCAPE '!'"))
            .ShouldNotBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Predicate walkers: hidden columns, calls, stars and aggregate levels are validated")]
    [InlineData("SELECT -'1' COLLATE binary FROM t")]
    [InlineData("SELECT absent IS TRUE FROM t")]
    [InlineData("SELECT flag IS DISTINCT FROM absent FROM t")]
    [InlineData("SELECT name LIKE pattern ESCAPE absent FROM t")]
    [InlineData("SELECT name LIKE pattern ESCAPE NO_SUCH_FUNCTION() FROM t")]
    [InlineData("SELECT * IS TRUE FROM t")]
    [InlineData("SELECT 1 IS DISTINCT FROM * FROM t")]
    [InlineData("SELECT name LIKE pattern ESCAPE * FROM t")]
    [InlineData("SELECT COUNT(*) FROM t WHERE COUNT(*) IS DISTINCT FROM 0")]
    [InlineData("SELECT COUNT(*) FROM t WHERE (COUNT(*) = 1) IS TRUE")]
    public async Task Execute_InvalidNestedOperand_ShouldFailAdmissionAsync(string sql)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));
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
        (await RowsAsync(reader, "SELECT * FROM predicates")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, true, true, true });
        foreach (string values in new[] { "(2, NULL, 'a%', 1, 1)", "(2, TRUE, 'abc', 1, 1)", "(2, TRUE, 'a%', NULL, 1)" })
        { await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(reader, "INSERT INTO checks VALUES " + values)); }
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
        error.Message.ShouldContain("supports only format 25", Case.Sensitive);
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
    [InlineData("invariant")]
    public void Match_LongPattern_ShouldObserveCancellation(string name)
    {
        // Arrange
        var pattern = SqlLikePattern.Compile("%" + new string('a', 100_000) + "b", "!", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        // Act / Assert
        Should.Throw<OperationCanceledException>(() => pattern.Match(new string('a', 1_000_000), Collation.FromName(name), cancellation.Token));
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
