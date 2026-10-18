using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures SQL profile claims against the live engine's planner, executor, and storage.
/// </summary>
public sealed class SqlLanguageConformanceTests
{
    private static readonly string[] _seed =
    [
        "CREATE TABLE t (id INT PRIMARY KEY, name TEXT, age INT);",
        "INSERT INTO t VALUES (1, 'Ada', 36), (2, 'Grace', 45), (3, 'Alan', 41);",
    ];

    private static readonly string[] _orderingSeed =
    [
        "CREATE TABLE ordering_rows (id INT PRIMARY KEY, age INT);",
        "INSERT INTO ordering_rows VALUES (40, 20), (10, 10), (50, 20), (20, 30), (30, 10);",
    ];

    private static readonly object?[][] _orderingInsertionRows = [[40, 20], [10, 10], [50, 20], [20, 30], [30, 10]];

    /// <summary>Requires every advertised clause to have a case with correct live-engine results.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Profile: every advertised clause has a verified live execution case")]
    public async Task Profile_EveryAdvertisedClause_ShouldExecuteItsMappedCase()
    {
        // The profile drives enumeration. A newly advertised clause cannot silently escape this test.
        var cases = CreateCases();
        // #1117 adds RETURNING: exactly one over #1124's 49.
        SqlLanguageProfile.Instance.Clauses.Count().ShouldBe(50);
        RequireExecutionCases(SqlLanguageProfile.Instance.Clauses, cases);
        cases.Keys.Except(SqlLanguageProfile.Instance.Clauses).ShouldBeEmpty("Cases must describe the current profile.");

        foreach (string clause in SqlLanguageProfile.Instance.Clauses)
        {
            var executionCase = cases[clause];
            var parsed = new SqlQueryParser().Parse(executionCase.Statement).ShouldBeOfType<SqlQueryStatement>();
            parsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error, clause);
            executionCase.ContainsClause(parsed.SqlExpression).ShouldBeTrue($"The {clause} case must actually contain that clause.");

            await using var engine = SqlDatabaseEngine.Create("sql-profile", new SqlDatabaseEngineOptions());
            var database = await engine.CreateDatabaseAsync("audit");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            foreach (string setup in executionCase.Setup)
            {
                (await ExecuteAsync(session, setup)).Status.ShouldBe(DatabaseResultStatus.Success, $"{clause}: {setup}");
            }

            var result = await ExecuteAsync(session, executionCase.Statement);
            result.Status.ShouldBe(DatabaseResultStatus.Success, clause);
            await executionCase.Verify(session, result);
        }
    }

    /// <summary>Proves that an added profile entry cannot bypass the execution-case completeness guard.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Profile: adding an advertised clause without a case fails coverage")]
    public void Profile_AdditionalClauseWithoutExecutionCase_ShouldFailCoverage()
    {
        var clauses = SqlLanguageProfile.Instance.Clauses.Append("FUTURE SQL CLAUSE").ToArray();
        Should.Throw<ShouldAssertException>(() => RequireExecutionCases(clauses, CreateCases()))
            .Message.ShouldContain("FUTURE SQL CLAUSE", Case.Sensitive);
    }

    /// <summary>Measures the literal family through live execution without changing the clause count.</summary>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Literals: typed forms execute with their declared metadata")]
    [MemberData(nameof(SqlTypedLiteralTests.Literals), MemberType = typeof(SqlTypedLiteralTests))]
    public async Task Select_TypedLiteral_ShouldReturnValueAndMetadata(string typeName, string literal, DatabaseType type, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-literal-conformance", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        await ExecuteAsync(session, _seed[0]);
        await ExecuteAsync(session, _seed[1]);

        // Act
        await using var result = (await ExecuteAsync(session, $"SELECT {literal} FROM t WHERE id = 1;")).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Single().Type.ShouldBe(type, typeName);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { expected });
    }

    /// <summary>Restores the audit's valid conversion cases with target values and metadata.</summary>
    /// <param name="sql">The conversion to submit through the session text entry point.</param>
    /// <param name="expected">The expected value, whose runtime type differs from the operand.</param>
    /// <param name="type">The expected target metadata.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: audited valid conversions execute with target types")]
    [MemberData(nameof(SqlCastMatrixTests.FamilyConversions), MemberType = typeof(SqlCastMatrixTests))]
    [InlineData("SELECT CAST('42' AS INT) FROM t WHERE id = 1;", 42, DatabaseType.Int32)]
    [InlineData("SELECT CAST(42 AS TEXT) FROM t WHERE id = 1;", "42", DatabaseType.String)]
    public async Task Cast_AuditedValidConversion_ShouldReturnTargetValueAndMetadata(string sql, object expected, DatabaseType type)
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-cast", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, _seed[0]);
        await ExecuteAsync(session, _seed[1]);
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns[0].Type.ShouldBe(type);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    /// <summary>Reverses the aggregate audit exclusions with verified values and rejects ungrouped columns.</summary>
    /// <param name="projection">An aggregate projection from the original unsupported-forms audit.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SELECT: audited aggregate forms execute and ungrouped columns fail")]
    [InlineData("COUNT(age)")]
    [InlineData("COUNT(DISTINCT age)")]
    [InlineData("COUNT(ALL age)")]
    [InlineData("SUM(age)")]
    [InlineData("SUM(DISTINCT age)")]
    [InlineData("SUM(ALL age)")]
    [InlineData("AVG(age)")]
    [InlineData("MIN(age)")]
    [InlineData("MAX(age)")]
    [InlineData("COUNT(*), id")]
    [InlineData("COUNT(*) + 1")]
    public async Task Select_AuditedAggregateForms_ShouldExecuteWithGroupingValidation(string projection)
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-aggregate", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _seed) { await ExecuteAsync(session, setup); }
        await ExecuteAsync(session, "INSERT INTO t VALUES (4, 'Ada', 36);");
        (await RowsAsync(session, "SELECT COUNT(*) FROM t WHERE age > 40;")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        string sql = $"SELECT {projection} FROM t;";
        new SqlQueryParser().Parse(sql).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (projection == "COUNT(*), id")
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
            error.Message.ShouldContain("id", Case.Sensitive);
            error.Message.ShouldContain("GROUP BY", Case.Sensitive);
            return;
        }
        object expected = projection switch
        {
            "COUNT(age)" or "COUNT(ALL age)" => 4L,
            "COUNT(DISTINCT age)" => 3L,
            "SUM(age)" or "SUM(ALL age)" => 158m,
            "SUM(DISTINCT age)" => 122m,
            "AVG(age)" => 158m / 4m,
            "MIN(age)" => 36,
            "MAX(age)" => 45,
            "COUNT(*) + 1" => 5L,
            _ => throw new InvalidOperationException($"Missing expected value for {projection}."),
        };
        (await RowsAsync(session, sql)).ShouldHaveSingleItem()[0].ShouldBe(expected);
        (await RowsAsync(session, $"SELECT {projection} FROM t GROUP BY age - age;")).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    /// <summary>Applies the full Int64 count range consistently to every materialized SELECT shape.</summary>
    /// <param name="query">The ordered query producing three rows.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - LIMIT/OFFSET: Int64 counts preserve the row window for every plan shape")]
    [InlineData("SELECT id FROM window_rows ORDER BY id")]
    [InlineData("SELECT w.id FROM window_rows w INNER JOIN window_peers p ON w.id = p.id ORDER BY w.id")]
    [InlineData("SELECT ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'window_rows' ORDER BY ORDINAL_POSITION")]
    [InlineData("SELECT id FROM window_rows GROUP BY id ORDER BY id")]
    public async Task Select_Int64RowWindow_ShouldReturnExpectedRows(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-profile-window", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE window_rows (id INT, category INT, label TEXT)");
        await ExecuteAsync(session, "INSERT INTO window_rows VALUES (3, 1, 'c'), (1, 1, 'a'), (2, 2, 'b')");
        await ExecuteAsync(session, "CREATE TABLE window_peers (id INT)");
        await ExecuteAsync(session, "INSERT INTO window_peers VALUES (1), (2), (3)");
        (string Clause, long[] Expected)[] windows =
        [
            ("", [1, 2, 3]),
            ("LIMIT 0", []),
            ("LIMIT 1", [1]),
            ("LIMIT 2 OFFSET 1", [2, 3]),
            ("LIMIT 2147483647", [1, 2, 3]),
            ("LIMIT 2147483648", [1, 2, 3]),
            ("LIMIT 4294967295", [1, 2, 3]),
            ("LIMIT 4294967296", [1, 2, 3]),
            ("LIMIT 4294967297", [1, 2, 3]),
            ("LIMIT 9223372036854775807", [1, 2, 3]),
            ("OFFSET 0", [1, 2, 3]),
            ("OFFSET 3", []),
            ("OFFSET 2147483647", []),
            ("OFFSET 2147483648", []),
            ("OFFSET 4294967295", []),
            ("OFFSET 4294967296", []),
            ("OFFSET 4294967297", []),
            ("OFFSET 9223372036854775807", []),
            ("LIMIT 9223372036854775807 OFFSET 1", [2, 3]),
            ("LIMIT 1 OFFSET 4294967297", []),
            ("LIMIT 9223372036854775807 OFFSET 9223372036854775807", []),
        ];

        foreach (var (clause, expected) in windows)
        {
            // Act
            var rows = await RowsAsync(session, $"{query} {clause}");

            // Assert
            rows.Select(row => Convert.ToInt64(row[0])).ShouldBe(expected, clause);
        }
    }

    /// <summary>Keeps invalid count diagnostics unchanged before a row window executes.</summary>
    /// <param name="clause">The count clause.</param>
    /// <param name="count">The invalid count.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - LIMIT/OFFSET: negative and fractional counts retain their diagnostics")]
    [InlineData("LIMIT", "-1")]
    [InlineData("LIMIT", "1.5")]
    [InlineData("OFFSET", "-1")]
    [InlineData("OFFSET", "1.5")]
    public async Task Select_InvalidRowWindow_ShouldRetainDiagnostic(string clause, string count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sql-profile-window-invalid", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _seed) { await ExecuteAsync(session, setup); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"SELECT id FROM t {clause} {count}"));

        // Assert
        error.Message.ShouldBe($"{clause} requires a non-negative integer.");
    }

    private static void RequireExecutionCases(IEnumerable<string> clauses, IReadOnlyDictionary<string, ExecutionCase> cases)
        => clauses.Where(clause => !cases.ContainsKey(clause)).ShouldBeEmpty("Every advertised clause needs a live execution case.");

    /// <summary>Reverses the default and ordering audit failures with verified live results.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Partial clauses: defaults backfill or reject atomically and ORDER BY aliases and ordinals execute")]
    public async Task PartialForms_DefaultsAndOrdering_ShouldMatchMeasuredBoundaries()
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-partial", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _seed) { await ExecuteAsync(session, setup); }
        // #1121: the expression supplies a frozen value to old rows and omitted inserts.
        string alter = "ALTER TABLE t ADD COLUMN extra INT DEFAULT (1 + 2);";
        new SqlQueryParser().Parse(alter).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        await ExecuteAsync(session, alter);
        await ExpectRowsAsync(session, "SELECT extra FROM t ORDER BY id;", [[3], [3], [3]]);
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (4, 'new', 1);");
        await ExpectRowsAsync(session, "SELECT extra FROM t WHERE id = 4;", [[3]]);
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN literal_default INT DEFAULT 7;");
        await ExpectRowsAsync(session, "SELECT id, name, age, literal_default FROM t ORDER BY id;",
            [[1, "Ada", 36, 7], [2, "Grace", 45, 7], [3, "Alan", 41, 7], [4, "new", 1, 7]]);
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (5, 'next', 2);");
        await ExpectRowsAsync(session, "SELECT literal_default FROM t WHERE id = 5;", [[7]]);
        // #1024: invert the original failures; both complete results differ from insertion and scan order.
        object?[][] years = [[1], [2], [36], [41], [45]];
        RequireReordering(await RowsAsync(session, "SELECT age FROM t;"), [[36], [45], [41], [1], [2]], years);
        await ExpectRowsAsync(session, "SELECT age AS years FROM t ORDER BY years;", years);
        object?[][] reverseIds = [[5], [4], [3], [2], [1]];
        RequireReordering(await RowsAsync(session, "SELECT id FROM t;"), [[1], [2], [3], [4], [5]], reverseIds);
        await ExpectRowsAsync(session, "SELECT id FROM t ORDER BY 1 DESC;", reverseIds);
        // #1122: one measured case per new ALTER action, without a new named clause.
        await ExecuteAsync(session, "ALTER TABLE t ALTER literal_default SET DEFAULT 9;");
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (6);");
        await ExpectRowsAsync(session, "SELECT literal_default FROM t WHERE id = 6;", [[9]]);
        await ExecuteAsync(session, "ALTER TABLE t ALTER literal_default DROP DEFAULT;");
        await ExecuteAsync(session, "INSERT INTO t(id) VALUES (7);");
        await ExpectRowsAsync(session, "SELECT literal_default FROM t WHERE id = 7;", [[null]]);
        await ExecuteAsync(session, "UPDATE t SET literal_default = 8 WHERE id = 7;");
        await ExecuteAsync(session, "ALTER TABLE t ALTER literal_default SET NOT NULL;");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE t SET literal_default = NULL WHERE id = 7;"));
        await ExecuteAsync(session, "ALTER TABLE t ALTER literal_default DROP NOT NULL;");
        await ExecuteAsync(session, "UPDATE t SET literal_default = NULL WHERE id = 7;");
        await ExecuteAsync(session, "ALTER TABLE t RENAME literal_default TO renamed_value;");
        await ExpectRowsAsync(session, "SELECT renamed_value FROM t WHERE id = 6;", [[9]]);
        await ExecuteAsync(session, "ALTER TABLE t RENAME TO renamed_table;");
        await ExpectRowsAsync(session, "SELECT renamed_value FROM renamed_table WHERE id = 6;", [[9]]);
    }

    /// <summary>Uses expanded projection positions without requiring unique output names.</summary>
    /// <param name="projection">The wildcard or duplicate-name projection.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ORDER BY: ordinals address expanded and duplicate-name projections")]
    [InlineData("*")]
    [InlineData("id AS value, age AS value")]
    public async Task OrderBy_ExpandedOrDuplicateProjection_ShouldResolveOrdinals(string projection)
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-order-projection", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _orderingSeed) { await ExecuteAsync(session, setup); }

        object?[][] expected = [[30, 10], [10, 10], [50, 20], [40, 20], [20, 30]];
        RequireReordering(await RowsAsync(session, $"SELECT {projection} FROM ordering_rows;"), _orderingInsertionRows, expected);
        await ExpectRowsAsync(session, $"SELECT {projection} FROM ordering_rows ORDER BY 2 ASC, 1 DESC;", expected);
    }

    /// <summary>Resolves an alias once while its projection still reads the same-named source column.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ORDER BY: same-name alias uses its computed projection without recursion")]
    public async Task OrderBy_AliasSharingItsOperandName_ShouldUseComputedOutput()
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-order-self", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _orderingSeed) { await ExecuteAsync(session, setup); }

        object?[][] expected = [[20, 70L], [50, 80L], [40, 80L], [30, 90L], [10, 90L]];
        object?[][] inserted = [[40, 80L], [10, 90L], [50, 80L], [20, 70L], [30, 90L]];
        RequireReordering(await RowsAsync(session, "SELECT id, 100 - age AS age FROM ordering_rows;"), inserted, expected);
        await ExpectRowsAsync(session, "SELECT id, 100 - age AS age FROM ordering_rows ORDER BY age ASC, 1 DESC;", expected);
    }

    /// <summary>Uses the same alias-first rule for an aggregate alias nested in an ordering expression.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ORDER BY: grouped nested alias wins over a source column")]
    public async Task OrderBy_GroupedAliasCollision_ShouldPreferAggregateOutput()
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-order-group", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _orderingSeed) { await ExecuteAsync(session, setup); }

        const string query = "SELECT ordering_rows.age AS source_age, SUM(id) AS age FROM ordering_rows GROUP BY ordering_rows.age";
        object?[][] expected = [[30, 20m], [10, 40m], [20, 90m]];
        object?[][] inserted = [[20, 90m], [10, 40m], [30, 20m]];
        RequireReordering(await RowsAsync(session, query), inserted, expected);
        await ExpectRowsAsync(session, query + " ORDER BY age + 1;", expected);
    }

    /// <summary>Rejects ambiguous aliases and keeps projection aliases out of source-expression scope.</summary>
    /// <param name="query">The query whose name binding is invalid.</param>
    /// <param name="message">The precise binding error.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ORDER BY: ambiguous aliases and alias chains fail binding")]
    [InlineData("SELECT id AS value, age AS value FROM ordering_rows ORDER BY value;", "Ambiguous ORDER BY alias 'value'.")]
    [InlineData("SELECT id AS value, age AS value FROM ordering_rows ORDER BY value + 1;", "Ambiguous ORDER BY alias 'value'.")]
    [InlineData("SELECT age AS value, SUM(id) AS value FROM ordering_rows GROUP BY age ORDER BY value;", "Ambiguous ORDER BY alias 'value'.")]
    [InlineData("SELECT age AS years, years + 1 AS next_year FROM ordering_rows ORDER BY next_year;", "Unknown column 'years'.")]
    [InlineData("SELECT age AS years FROM ordering_rows WHERE years > 0 ORDER BY years;", "Unknown column 'years'.")]
    public async Task OrderBy_InvalidAliasBinding_ShouldFailPrecisely(string query, string message)
    {
        await using var engine = SqlDatabaseEngine.Create("sql-profile-order-invalid", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("audit");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (string setup in _orderingSeed) { await ExecuteAsync(session, setup); }

        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, query))).ShouldHaveMessage(message);
    }

    // The single function guard: add execution evidence here whenever the profile gains a name.
    private static readonly IReadOnlyDictionary<string, FunctionCase> _functionCases = CreateFunctionCases();

    private static IReadOnlyDictionary<string, FunctionCase> CreateFunctionCases()
    {
        var cases =        new Dictionary<string, FunctionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["COUNT"] = new("COUNT(*)", 3L),
            ["SUM"] = new("SUM(age)", 122m),
            ["AVG"] = new("AVG(age)", 122m / 3m),
            ["MIN"] = new("MIN(age)", 36),
            ["MAX"] = new("MAX(age)", 45),
            ["STRING_AGG"] = new("STRING_AGG(name, ',' ORDER BY id)", "Ada,Grace,Alan"),
            ["LISTAGG"] = new("LISTAGG(name, ',') WITHIN GROUP (ORDER BY id)", "Ada,Grace,Alan"),
            ["COALESCE"] = new("COALESCE(NULL, 7)", 7L),
            ["CAST"] = new("CAST('42' AS INT)", 42),
            ["UPPER"] = new("UPPER('Ada')", "ADA"),
            ["LOWER"] = new("LOWER('Ada')", "ada"),
            ["LENGTH"] = new("LENGTH('Ada')", 3L),
            ["ABS"] = new("ABS(-2)", 2L),
        };
        foreach (var item in SqlT1ScalarTests.Cases())
        {
            cases[(string)item[0]!] = new FunctionCase((string)item[1]!, item[2], (DatabaseType)item[4]!, "scalar_rows");
        }
        return cases;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: every declared function has passing live evidence")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCase()
    {
        // Arrange
        RequireFunctionCoverage(SqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys);
        await using var engine = SqlDatabaseEngine.Create("sql-functions", new() { TimeProvider = new ScalarClock(SqlT1ScalarTests.Instant) });
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in _seed) { await ExecuteAsync(session, setup); }

        await ExecuteAsync(session, "CREATE TABLE scalar_rows (s TEXT, i BIGINT, n DECIMAL, d DATE);");
        await ExecuteAsync(session, "INSERT INTO scalar_rows VALUES ('A😀b', -2, 2.5, DATE '2024-03-04');");

        // Act / Assert
        foreach (string name in SqlLanguageProfile.Instance.Functions.ToArray())
        {
            var execution = _functionCases[name];
            await using var result = (await ExecuteAsync(session, $"SELECT {execution.Expression} FROM {execution.Source}")).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns[0].Type.ShouldBe(execution.Type ?? SqlValue.FromObject(execution.Expected).Type, name);
            var rows = await ReadRowsAsync(result);
            rows.ShouldNotBeEmpty(name);
            foreach (var row in rows) { SqlT1ScalarTests.VerifyValue(row[0], execution.Expected); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: coverage fails for an unmeasured declaration or a retired case")]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(bool addedDeclaration)
    {
        // Arrange
        var declarations = SqlLanguageProfile.Instance.Functions.ToArray();
        var cases = _functionCases.Keys;
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            addedDeclaration ? declarations.Append("FUTURE") : declarations,
            addedDeclaration ? cases : cases.Append("RETIRED")));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases)
    {
        declarations.Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("Declared functions need live cases.");
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("Retired functions cannot retain positive cases.");
    }

    private sealed record FunctionCase(string Expression, object? Expected, DatabaseType? Type = null, string Source = "t");

    private static Dictionary<string, ExecutionCase> CreateCases() => new(StringComparer.Ordinal)
    {
        [SqlClauses.Explain] = new("EXPLAIN SELECT id FROM t WHERE age = 36;",
            [.. _seed, "CREATE INDEX by_age ON t (age);"], expression => expression is SqlExplainExpression,
            async (_, result) =>
            {
                var rows = await ReadRowsAsync(result);
                rows.ShouldContain(row => Equals(row[2], "IndexSeek") && Equals(row[4], "by_age") && Equals(row[5], "age = 36"));
            }),
        [SqlClauses.Select] = Query("SELECT id, age + 1 AS next_age FROM t ORDER BY id;", expression => expression is SqlSelectExpression,
            [[1, 37L], [2, 46L], [3, 42L]]),
        [SqlClauses.From] = Query("SELECT u.name FROM dbo.t AS u WHERE id = 1;", expression => expression is SqlSelectExpression { From.Count: 1 },
            [["Ada"]]),
        [SqlClauses.SetOperation] = Query("SELECT id FROM t WHERE id = 1 UNION SELECT id FROM t WHERE id = 2 ORDER BY id;",
            expression => expression is SqlSetOperationExpression { Operator: SqlSetOperator.Union }, [[1], [2]]),
        [SqlClauses.Intersect] = Query("SELECT id FROM t INTERSECT SELECT id FROM t WHERE id = 2;",
            expression => expression is SqlSetOperationExpression { Operator: SqlSetOperator.Intersect }, [[2]]),
        [SqlClauses.Except] = Query("SELECT id FROM t EXCEPT SELECT id FROM t WHERE id <> 2;",
            expression => expression is SqlSetOperationExpression { Operator: SqlSetOperator.Except }, [[2]]),
        [SqlClauses.Cte] = new("WITH a AS (SELECT id FROM t WHERE age > 40), b AS (SELECT id FROM a) SELECT id FROM b ORDER BY id;", _seed,
            expression => expression is SqlSelectExpression { CommonTables.Count: 2 },
            async (session, result) =>
            {
                CheckRows(await ReadRowsAsync(result), [[2], [3]]);
                (await session.ExecuteAsync("WITH a AS (SELECT id FROM t WHERE age > 40) DELETE FROM t WHERE id IN (SELECT id FROM a);", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
                await ExpectRowsAsync(session, "SELECT id FROM t;", [[1]]);
            }),
        [SqlClauses.Subquery] = new("SELECT id, (SELECT MAX(age) FROM t) AS oldest FROM t WHERE id IN (SELECT id FROM t WHERE age > 40) AND EXISTS (SELECT id FROM t WHERE age = 36) ORDER BY id;", _seed,
            expression => expression is SqlSelectExpression select && select.Columns.Any(column => column.Expression is SqlSubqueryExpression),
            async (session, result) =>
            {
                CheckRows(await ReadRowsAsync(result), [[2, 45], [3, 45]]);
                await ExpectRowsAsync(session, "SELECT d.id FROM (SELECT id FROM t WHERE age > 40 ORDER BY id LIMIT 1) d;", [[2]]);
                await ExpectRowsAsync(session, "SELECT outer_t.id FROM t outer_t WHERE EXISTS (SELECT inner_t.id FROM t inner_t WHERE inner_t.id = outer_t.id AND inner_t.age > 40) ORDER BY outer_t.id;", [[2], [3]]);
                (await session.ExecuteAsync("UPDATE t SET age = age WHERE id IN (SELECT id FROM t WHERE id = 2);", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
            }),
        [SqlClauses.Join] = new("SELECT t.name, p.email FROM t INNER JOIN t copy ON t.id = copy.id LEFT JOIN profiles p ON t.id = p.user_id ORDER BY t.id;",
            [.. _seed, "CREATE TABLE profiles (user_id INT REFERENCES t(id), email TEXT);",
                "INSERT INTO profiles VALUES (1, 'ada@example.test'), (3, 'alan@example.test');"],
            expression => expression is SqlSelectExpression { From.Count: 1 } select && select.From[0].Joins.Count == 2 && select.From[0].Joins[1].JoinType == SqlJoinType.LeftOuter,
            async (session, result) =>
            {
                CheckRows(await ReadRowsAsync(result), [["Ada", "ada@example.test"], ["Grace", null], ["Alan", "alan@example.test"]]);
                await ExpectRowsAsync(session, "SELECT c.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLES t " +
                    "JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME WHERE t.TABLE_NAME = 'profiles' ORDER BY c.ORDINAL_POSITION;",
                    [["user_id"], ["email"]]);
            }),
        [SqlClauses.Using] = Query("SELECT id FROM t LEFT JOIN t copy USING (id) ORDER BY id;",
            expression => expression is SqlSelectExpression select && select.From[0].Joins[0].UsingColumns.Count == 1,
            [[1], [2], [3]]),
        [SqlClauses.Where] = Query("SELECT id FROM t WHERE age > 40 AND name LIKE 'G%' ORDER BY id;", expression => expression is SqlSelectExpression { Where: not null },
            [[2]]),
        [SqlClauses.Collate] = Query("SELECT id FROM t WHERE name = 'ada' COLLATE case_insensitive;",
            expression => expression is SqlSelectExpression { Where: SqlBinaryExpression { Right: SqlCollateExpression } }, [[1]]),
        [SqlClauses.GroupBy] = Query("SELECT age > 40, COUNT(*), SUM(age) FROM t GROUP BY age > 40 ORDER BY age > 40;",
            expression => expression is SqlSelectExpression { GroupBy.Count: 1 }, [[false, 1L, 36m], [true, 2L, 86m]]),
        [SqlClauses.Having] = Query("SELECT age > 40, COUNT(*), SUM(age) FROM t WHERE age > 35 GROUP BY age > 40 HAVING SUM(age) > 50;",
            expression => expression is SqlSelectExpression { Having: not null }, [[true, 2L, 86m]]),
        [SqlClauses.OrderBy] = new("SELECT id, age AS years FROM ordering_rows ORDER BY years + 1 ASC NULLS LAST, 1 DESC;",
            ["CREATE TABLE ordering_rows (id INT PRIMARY KEY, age INT);",
             "INSERT INTO ordering_rows VALUES (40, 20), (10, NULL), (50, 20), (20, NULL), (30, 10);"],
            expression => expression is SqlSelectExpression { OrderBy.Count: 2 } select && select.OrderBy[0].NullPlacement == SqlNullPlacement.Last,
            async (session, result) =>
            {
                object?[][] expected = [[30, 10], [50, 20], [40, 20], [20, null], [10, null]];
                RequireReordering(await RowsAsync(session, "SELECT id, age FROM ordering_rows;"),
                    [[40, 20], [10, null], [50, 20], [20, null], [30, 10]], expected);
                CheckRows(await ReadRowsAsync(result), expected);
                await ExpectRowsAsync(session, "SELECT id, age FROM ordering_rows ORDER BY age + 1 ASC NULLS LAST, id DESC;", expected);
            }),
        [SqlClauses.Limit] = Query("SELECT id FROM t ORDER BY id LIMIT 2;", expression => expression is SqlSelectExpression { LimitSyntaxKind: SqlLimitSyntaxKind.Limit },
            [[1], [2]]),
        [SqlClauses.Fetch] = Query("SELECT id FROM t ORDER BY id OFFSET 1 ROW FETCH NEXT 2 ROWS ONLY;",
            expression => expression is SqlSelectExpression { LimitSyntaxKind: SqlLimitSyntaxKind.Fetch }, [[2], [3]]),
        [SqlClauses.Top] = Query("SELECT TOP (2) id FROM t ORDER BY id;",
            expression => expression is SqlSelectExpression { LimitSyntaxKind: SqlLimitSyntaxKind.Top }, [[1], [2]]),
        [SqlClauses.Offset] = Query("SELECT id FROM t ORDER BY id OFFSET 1;", expression => expression is SqlSelectExpression { Offset: not null },
            [[2], [3]]),
        [SqlClauses.Case] = Query("SELECT CASE WHEN age > 40 THEN 'senior' ELSE 'junior' END, CASE id WHEN 1 THEN 'first' ELSE 'later' END FROM t ORDER BY id;",
            expression => expression is SqlSelectExpression select && select.Columns.All(column => column.Expression is SqlCaseExpression),
            [["junior", "first"], ["senior", "later"], ["senior", "later"]]),
        [SqlClauses.Cast] = new("SELECT CAST('42' AS INT), CAST(age AS TEXT) FROM t ORDER BY id;", _seed,
            expression => expression is SqlSelectExpression select && select.Columns.All(column => column.Expression is SqlCastExpression),
            async (_, result) =>
            {
                var resultSet = result.ShouldBeAssignableTo<DatabaseResultSet>();
                resultSet.Columns[0].Type.ShouldBe(DatabaseType.Int32);
                resultSet.Columns[1].Type.ShouldBe(DatabaseType.String);
                CheckRows(await ReadRowsAsync(resultSet), [[42, "36"], [42, "45"], [42, "41"]]);
            }),
        [SqlClauses.Insert] = new("INSERT INTO t (id, name, age) VALUES (4, 'Barbara', 33);", _seed,
            expression => expression is SqlInsertExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT name, age FROM t WHERE id = 4;", [["Barbara", 33]]);
            }),
        [SqlClauses.Values] = new("INSERT INTO t VALUES (4, 'Barbara', 33), (5, 'Edsger', 42);", _seed,
            expression => expression is SqlInsertExpression { Values.Count: 2 },
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(2);
                await ExpectRowsAsync(session, "SELECT name FROM t WHERE id > 3 ORDER BY id;", [["Barbara"], ["Edsger"]]);
            }),
        [SqlClauses.Update] = new("UPDATE t SET age = age + 10 WHERE id = 1;", _seed,
            expression => expression is SqlUpdateExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT age FROM t ORDER BY id;", [[46], [45], [41]]);
            }),
        [SqlClauses.Returning] = new("UPDATE t SET age = age + 1 WHERE id = 1 RETURNING id, age;", _seed,
            expression => expression is SqlUpdateExpression { Returning: not null },
            async (_, result) =>
            {
                result.ShouldBeAssignableTo<DatabaseResultSet>().Columns.Select(column => (column.Name, column.Type))
                    .ShouldBe([("id", DatabaseType.Int32), ("age", DatabaseType.Int32)]);
                result.AffectedCount.ShouldBe(1);
                CheckRows(await ReadRowsAsync(result), [[1, 37]]);
            }),
        [SqlClauses.Delete] = new("DELETE FROM t WHERE id = 2;", _seed,
            expression => expression is SqlDeleteExpression,
            async (session, result) =>
            {
                result.AffectedCount.ShouldBe(1);
                await ExpectRowsAsync(session, "SELECT id FROM t ORDER BY id;", [[1], [3]]);
            }),
        [SqlClauses.Identity] = new("CREATE TABLE identities (id BIGINT GENERATED BY DEFAULT AS IDENTITY (START WITH 100 INCREMENT BY 5), label TEXT);", [],
            expression => expression is SqlCreateTableExpression create && create.Columns.Any(column => column.Identity is not null),
            async (session, _) =>
            {
                await ExecuteAsync(session, "INSERT INTO identities (label) VALUES ('a'), ('b');");
                await using var rows = (DatabaseResultSet)await ExecuteAsync(session, "SELECT id FROM identities ORDER BY id;");
                (await ReadRowsAsync(rows)).ShouldBe(new object?[][] { [100L], [105L] });
            }),
        [SqlClauses.CreateTable] = new("CREATE TABLE created (id INT PRIMARY KEY, label NVARCHAR(MAX) DEFAULT 'new', f DOUBLE PRECISION, n NATIONAL CHARACTER(3), stamp TIMESTAMP WITH TIME ZONE);", [],
            expression => expression is SqlCreateTableExpression,
            async (session, _) =>
            {
                await ExecuteAsync(session, "INSERT INTO created (id) VALUES (7);");
                await ExpectRowsAsync(session, "SELECT id, label FROM created;", [[7, "new"]]);
                await ExecuteAsync(session, "UPDATE created SET n = 'ab', f = 1.5;");
                await ExpectRowsAsync(session, "SELECT n, f FROM created;", [["ab ", 1.5d]]);
                await ExpectRowsAsync(session, "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'created' ORDER BY ORDINAL_POSITION;",
                    [["INTEGER"], ["CHARACTER VARYING"], ["DOUBLE PRECISION"], ["CHARACTER"], ["TIMESTAMP WITH TIME ZONE"]]);
            }),
        [SqlClauses.AlterTable] = new("ALTER TABLE t ADD COLUMN extra INT NOT NULL DEFAULT 7;", _seed,
            expression => expression is SqlAlterTableExpression { Action: SqlAlterAddColumnAction }, VerifyAlterAsync),
        [SqlClauses.DropTable] = new("DROP TABLE t;", _seed,
            expression => expression is SqlDropTableExpression,
            async (session, _) =>
            {
                session.Database.Catalog.TryGetTable("dbo", "t", out var dropped).ShouldBeFalse();
                dropped.ShouldBeNull();
                (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "SELECT * FROM t;")))
                    .Message.ShouldContain("does not exist", Case.Sensitive);
                (await ExecuteAsync(session, "DROP TABLE IF EXISTS t;")).Status.ShouldBe(DatabaseResultStatus.Success);
            }),
        [SqlClauses.CreateView] = new("CREATE VIEW v AS SELECT id FROM t;", _seed,
            expression => expression is SqlCreateViewExpression,
            async (session, _) => await ExpectRowsAsync(session, "SELECT id FROM v ORDER BY id LIMIT 1;", [[1]])),
        [SqlClauses.DropView] = new("DROP VIEW v;", ["CREATE TABLE t (id INT);", "INSERT INTO t VALUES (1);", "CREATE VIEW v AS SELECT id FROM t;"],
            expression => expression is SqlDropViewExpression,
            async (session, _) =>
            {
                session.Database.Catalog.TryGetView("dbo", "v", out var absentView).ShouldBeFalse();
                await ExpectRowsAsync(session, "SELECT id FROM t;", [[1]]);
            }),
        [SqlClauses.CreateSchema] = new("CREATE SCHEMA sales;", [],
            expression => expression is SqlCreateSchemaExpression,
            async (session, _) =>
            {
                session.Database.Catalog.Namespaces.ShouldContain(schema => schema.Name == "sales");
                await ExecuteAsync(session, "CREATE TABLE sales.orders(id INT);");
                await ExpectRowsAsync(session, "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = 'sales';", [["sales"]]);
            }),
        [SqlClauses.DropSchema] = new("DROP SCHEMA sales CASCADE;", ["CREATE SCHEMA sales;", "CREATE TABLE sales.orders(id INT);"],
            expression => expression is SqlDropSchemaExpression { Cascade: true },
            async (session, _) =>
            {
                session.Database.Catalog.Namespaces.ShouldNotContain(schema => schema.Name == "sales");
                session.Database.Catalog.TryGetTable("sales", "orders", out var droppedTable).ShouldBeFalse();
                await ExecuteAsync(session, "DROP SCHEMA IF EXISTS sales;");
            }),
        [SqlClauses.CreateIndex] = new("CREATE UNIQUE INDEX ix_name ON t(name);", _seed,
            expression => expression is SqlCreateIndexExpression { IsUnique: true },
            async (session, _) =>
            {
                var catalog = session.Database.Catalog;
                catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
                catalog.GetIndexes(table!.ObjectId).ShouldContain(index => index.Name == "ix_name" && index.IsUnique);
                await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO t VALUES (4, 'Ada', 1);"));
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE name = 'Ada';", [[1]]);
            }),
        [SqlClauses.DropIndex] = new("DROP INDEX ix_age ON t;", [.. _seed, "CREATE INDEX ix_age ON t(age);"],
            expression => expression is SqlDropIndexExpression,
            async (session, _) =>
            {
                var catalog = session.Database.Catalog;
                catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
                catalog.GetIndexes(table!.ObjectId).ShouldNotContain(index => index.Name == "ix_age");
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE age = 36;", [[1]]);
                (await ExecuteAsync(session, "DROP INDEX IF EXISTS ix_age ON t;")).Status.ShouldBe(DatabaseResultStatus.Success);
            }),
        [SqlClauses.Constraint] = new("CREATE TABLE c (code INT, CONSTRAINT uq_code UNIQUE(code));", [],
            expression => expression is SqlCreateTableExpression create && create.Constraints.Any(constraint => constraint.Name == "uq_code"), VerifyUniqueAsync),
        [SqlClauses.UniqueConstraint] = new("CREATE TABLE c (code INT UNIQUE);", [],
            expression => HasConstraint(expression, SqlConstraintKind.Unique), VerifyUniqueAsync),
        [SqlClauses.Check] = new("CREATE TABLE c (code INT CHECK(code > 0));", [],
            expression => HasConstraint(expression, SqlConstraintKind.Check),
            async (session, _) =>
            {
                await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
                var error = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (0);"));
                error.ConstraintKind.ShouldBe("CHECK");
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
            }),
        [SqlClauses.ForeignKey] = ForeignKeyCase("CREATE TABLE c (pid INT, CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES t(id));", SqlReferentialAction.NoAction, VerifyReferenceAsync),
        [SqlClauses.References] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id));", SqlReferentialAction.NoAction, VerifyReferenceAsync),
        [SqlClauses.Cascade] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE CASCADE);", SqlReferentialAction.Cascade,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 1;");
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c WHERE pid = 1;", [[0L]]);
                await ExpectRowsAsync(session, "SELECT pid FROM c;", [[null]]);
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM t;", [[2L]]);
            }),
        [SqlClauses.Restrict] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE RESTRICT);", SqlReferentialAction.Restrict,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                var error = await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM t WHERE id = 1;"));
                error.ConstraintKind.ShouldBe("FOREIGN KEY");
                await ExpectRowsAsync(session, "SELECT pid FROM c WHERE pid = 1;", [[1]]);
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE id = 1;", [[1]]);
            }),
        [SqlClauses.SetNull] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE SET NULL);", SqlReferentialAction.SetNull,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 1;");
                await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c WHERE pid IS NULL;", [[2L]]);
            }),
        [SqlClauses.SetDefault] = ForeignKeyCase("CREATE TABLE c (pid INT DEFAULT 2 REFERENCES t(id) ON DELETE SET DEFAULT);", SqlReferentialAction.SetDefault,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 1;");
                await ExpectRowsAsync(session, "SELECT pid FROM c WHERE pid IS NOT NULL;", [[2]]);
            }),
        [SqlClauses.NoAction] = ForeignKeyCase("CREATE TABLE c (pid INT REFERENCES t(id) ON DELETE NO ACTION);", SqlReferentialAction.NoAction,
            async (session, result) =>
            {
                await VerifyReferenceAsync(session, result);
                await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM t WHERE id = 1;"));
                await ExpectRowsAsync(session, "SELECT pid FROM c WHERE pid = 1;", [[1]]);
            }),
        [SqlClauses.Begin] = new("BEGIN;", _seed,
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Begin }, VerifyBeginAsync),
        [SqlClauses.Commit] = new("COMMIT;", [.. _seed, "BEGIN;", "INSERT INTO t VALUES (4, 'new', 1);"],
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Commit },
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldBeNull();
                await using var observer = await session.Database.CreateSessionAsync();
                await ExpectRowsAsync(observer, "SELECT id FROM t WHERE id = 4;", [[4]]);
            }),
        [SqlClauses.Rollback] = new("ROLLBACK;", [.. _seed, "BEGIN;", "INSERT INTO t VALUES (4, 'new', 1);", "DELETE FROM t WHERE id = 2;"],
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Rollback },
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldBeNull();
                await using var observer = await session.Database.CreateSessionAsync();
                await ExpectRowsAsync(observer, "SELECT id FROM t ORDER BY id;", [[1], [2], [3]]);
            }),
        [SqlClauses.Transaction] = new("BEGIN TRANSACTION;", _seed,
            expression => expression is SqlTransactionExpression { CommandType: SqlQueryCommandType.Begin } && expression.Text!.Contains("TRANSACTION", StringComparison.Ordinal),
            async (session, _) =>
            {
                session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
                await ExecuteAsync(session, "INSERT INTO t VALUES (4, 'new', 1);");
                await ExecuteAsync(session, "COMMIT TRANSACTION;");
                await ExecuteAsync(session, "BEGIN TRANSACTION;");
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 4;");
                await ExecuteAsync(session, "ROLLBACK TRANSACTION;");
                session.CurrentTransaction.ShouldBeNull();
                await ExpectRowsAsync(session, "SELECT id FROM t WHERE id = 4;", [[4]]);
                await using var observer = await session.Database.CreateSessionAsync();
                await ExecuteAsync(session, "START TRANSACTION;");
                await ExecuteAsync(session, "INSERT INTO t VALUES (5, 'start', 2);");
                await ExpectRowsAsync(observer, "SELECT id FROM t WHERE id = 5;", []);
                await ExecuteAsync(session, "END;");
                await ExpectRowsAsync(observer, "SELECT id FROM t WHERE id = 5;", [[5]]);
                await ExecuteAsync(session, "BEGIN WORK;");
                await ExecuteAsync(session, "UPDATE t SET name = 'changed' WHERE id = 5;");
                await ExecuteAsync(session, "DELETE FROM t WHERE id = 4;");
                await ExecuteAsync(session, "INSERT INTO t VALUES (6, 'rolled back', 3);");
                await ExecuteAsync(session, "ROLLBACK WORK;");
                session.CurrentTransaction.ShouldBeNull();
                await ExpectRowsAsync(observer, "SELECT id, name FROM t WHERE id >= 4 ORDER BY id;", [[4, "new"], [5, "start"]]);
            }),
    };

    private static ExecutionCase Query(string statement, Func<SqlQueryExpression, bool> containsClause, object?[][] expected)
        => new(statement, _seed, containsClause, async (_, result) => CheckRows(await ReadRowsAsync(result), expected));

    private static ExecutionCase ForeignKeyCase(string statement, SqlReferentialAction action, Func<SqlDatabaseSession, DatabaseResult, Task> verify)
        => new(statement, _seed, expression => expression is SqlCreateTableExpression create &&
            create.Constraints.Any(constraint => constraint.Kind == SqlConstraintKind.ForeignKey && constraint.ReferencedTable is not null && constraint.OnDelete == action), verify);

    private static bool HasConstraint(SqlQueryExpression expression, SqlConstraintKind kind)
        => expression is SqlCreateTableExpression create && create.Constraints.Any(constraint => constraint.Kind == kind);

    private static async Task VerifyAlterAsync(SqlDatabaseSession session, DatabaseResult result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        await ExpectRowsAsync(session, "SELECT id, name, age, extra FROM t ORDER BY id;",
            [[1, "Ada", 36, 7], [2, "Grace", 45, 7], [3, "Alan", 41, 7]]);
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN nullable_extra INT;");
        await ExpectRowsAsync(session, "SELECT nullable_extra FROM t ORDER BY id;", [[null], [null], [null]]);
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN nullable_extra;");
        await ExecuteAsync(session, "INSERT INTO t (id, name, age) VALUES (4, 'new', 1);");
        await ExpectRowsAsync(session, "SELECT id, name, age, extra FROM t WHERE id = 4;", [[4, "new", 1, 7]]);
        await ExecuteAsync(session, "ALTER TABLE t ADD CONSTRAINT positive CHECK(extra > 0);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE t SET extra = 0 WHERE id = 1;")))
            .ConstraintKind.ShouldBe("CHECK");
        await ExecuteAsync(session, "ALTER TABLE t DROP CONSTRAINT positive;");
        await ExecuteAsync(session, "UPDATE t SET extra = 0 WHERE id = 1;");
        await ExpectRowsAsync(session, "SELECT extra FROM t WHERE id = 1;", [[0]]);
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra;");
        await ExpectRowsAsync(session, "SELECT * FROM t WHERE id = 1;", [[1, "Ada", 36]]);
    }

    private static async Task VerifyUniqueAsync(SqlDatabaseSession session, DatabaseResult result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (1);")))
            .ConstraintKind.ShouldBe("UNIQUE");
        // This dialect's unique index treats NULL as a key, so a second NULL is also a duplicate.
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (NULL);")))
            .ConstraintKind.ShouldBe("UNIQUE");
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
    }

    private static async Task VerifyReferenceAsync(SqlDatabaseSession session, DatabaseResult result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        await ExecuteAsync(session, "INSERT INTO c VALUES (1), (NULL);");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO c VALUES (99);")))
            .ConstraintKind.ShouldBe("FOREIGN KEY");
        (await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE t SET id = 99 WHERE id = 1;")))
            .ConstraintKind.ShouldBe("FOREIGN KEY");
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM c;", [[2L]]);
    }

    private static async Task VerifyBeginAsync(SqlDatabaseSession session, DatabaseResult result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
        await ExecuteAsync(session, "INSERT INTO t VALUES (4, 'new', 1);");
        await using var observer = await session.Database.CreateSessionAsync();
        await ExpectRowsAsync(observer, "SELECT COUNT(*) FROM t;", [[3L]]);
        await ExpectRowsAsync(session, "SELECT COUNT(*) FROM t;", [[4L]]);
        await ExecuteAsync(session, "ROLLBACK;");
        await ExpectRowsAsync(observer, "SELECT COUNT(*) FROM t;", [[3L]]);
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();

    private static async Task ExpectRowsAsync(SqlDatabaseSession session, string statement, object?[][] expected)
        => CheckRows(await RowsAsync(session, statement), expected);

    /// <summary>Prevents correct-order expectations from passing when the executor merely preserves input order.</summary>
    private static void RequireReordering(List<object?[]> scanned, object?[][] inserted, object?[][] expected)
    {
        static bool SameRows(IEnumerable<object?[]> left, IEnumerable<object?[]> right)
            => left.Count() == right.Count() && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

        SameRows(expected, scanned).ShouldBeFalse("The expected order must differ from the observed storage scan order.");
        SameRows(expected, inserted).ShouldBeFalse("The expected order must differ from insertion order.");
    }

    private static void CheckRows(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++) { actual[i].ShouldBe(expected[i]); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string statement)
        => await ReadRowsAsync(await ExecuteAsync(session, statement));

    private static async Task<List<object?[]>> ReadRowsAsync(DatabaseResult databaseResult)
    {
        await using var result = databaseResult.ShouldBeAssignableTo<DatabaseResultSet>();
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private sealed record ExecutionCase(
        string Statement,
        string[] Setup,
        Func<SqlQueryExpression, bool> ContainsClause,
        Func<SqlDatabaseSession, DatabaseResult, Task> Verify);
}
