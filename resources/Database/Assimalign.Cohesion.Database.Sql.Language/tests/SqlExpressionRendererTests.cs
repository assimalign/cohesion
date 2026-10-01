using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Proves the canonical-persistence invariant behind stored definitions: for every tree the
/// parser accepts, parsing the rendered text yields the same tree (positions aside), and the
/// rendered text is a fixed point. The CHECK corpus pins the canonical spelling of every form a
/// CHECK constraint accepts.
/// </summary>
public sealed class SqlExpressionRendererTests
{
    /// <summary>
    /// Every expression form a CHECK accepts, as written and as the engine stores it.
    /// </summary>
    public static IEnumerable<object[]> CheckCorpus() =>
    [
        // Comparison and arithmetic operators, precedence and associativity.
        ["qty > 0", "qty > 0"],
        ["QTY>0", "QTY > 0"],
        ["qty<>0 and qty!=1", "qty <> 0 AND qty <> 1"],
        ["qty <= 10 OR qty >= 20", "qty <= 10 OR qty >= 20"],
        ["(qty + 1) * 2 > 0", "(qty + 1) * 2 > 0"],
        ["qty + 1 * 2 > 0", "qty + 1 * 2 > 0"],
        ["qty - (1 - 2) <> 0", "qty - (1 - 2) <> 0"],
        ["((qty - 1) - 2) <> 0", "qty - 1 - 2 <> 0"],
        ["qty / 2 % 3 >= 0", "qty / 2 % 3 >= 0"],
        ["qty / (2 % 3) >= 0", "qty / (2 % 3) >= 0"],
        ["name || 'x' = 'ax'", "name || 'x' = 'ax'"],
        ["name || ('x' || 'y') = 'axy'", "name || ('x' || 'y') = 'axy'"],
        ["(qty > 0) = TRUE", "(qty > 0) = TRUE"],
        // Logical operators and NOT.
        ["qty > 0 AND (qty < 10 OR qty > 20)", "qty > 0 AND (qty < 10 OR qty > 20)"],
        ["(qty > 0 AND qty < 10) OR qty > 20", "qty > 0 AND qty < 10 OR qty > 20"],
        ["qty > 0 OR (qty < 10 OR qty > 20)", "qty > 0 OR (qty < 10 OR qty > 20)"],
        ["NOT (qty > 0 AND qty < 10)", "NOT (qty > 0 AND qty < 10)"],
        ["NOT qty > 0", "NOT qty > 0"],
        ["not not flag", "NOT NOT flag"],
        ["(NOT flag) = FALSE", "(NOT flag) = FALSE"],
        // Signs, including negative numbers and ISO unary plus.
        ["qty > -1", "qty > -1"],
        ["price >= -0.5", "price >= -0.5"],
        ["qty > -9223372036854775808", "qty > -9223372036854775808"],
        ["qty >= - -1", "qty >= -(-1)"],
        ["-qty <= 0", "-qty <= 0"],
        ["-(qty + 1) < 0", "-(qty + 1) < 0"],
        ["qty < +5", "qty < 5"],
        ["+qty > 0", "+qty > 0"],
        ["+(qty + 1) > 0", "+(qty + 1) > 0"],
        ["+(1) < qty", "+(1) < qty"],
        ["- +qty < 0", "-(+qty) < 0"],
        ["qty - -1 > 0", "qty - -1 > 0"],
        // CASE, simple and searched.
        ["CASE WHEN qty > 0 THEN TRUE ELSE FALSE END", "CASE WHEN qty > 0 THEN TRUE ELSE FALSE END"],
        ["case status when 'a' then qty > 0 when 'b' then qty < 0 end", "CASE status WHEN 'a' THEN qty > 0 WHEN 'b' THEN qty < 0 END"],
        ["(CASE WHEN flag THEN 1 END) = 1", "CASE WHEN flag THEN 1 END = 1"],
        // IN, BETWEEN, LIKE, IS NULL.
        ["status IN ('a','b', 'it''s')", "status IN ('a', 'b', 'it''s')"],
        ["status NOT IN ('x')", "status NOT IN ('x')"],
        ["qty + 1 IN (1, 2 + 3)", "qty + 1 IN (1, 2 + 3)"],
        ["qty BETWEEN 1 AND 10", "qty BETWEEN 1 AND 10"],
        ["qty NOT BETWEEN -1 AND (1 + 1)", "qty NOT BETWEEN -1 AND 1 + 1"],
        ["qty BETWEEN 1 AND 10 AND flag", "qty BETWEEN 1 AND 10 AND flag"],
        ["name LIKE 'a%'", "name LIKE 'a%'"],
        ["name NOT LIKE '_b%'", "name NOT LIKE '_b%'"],
        ["name LIKE ('a' || '%')", "name LIKE ('a' || '%')"],
        ["name LIKE 'A%' COLLATE case_insensitive", "name LIKE 'A%' COLLATE case_insensitive"],
        ["name IS NOT NULL", "name IS NOT NULL"],
        ["(qty IS NULL) IS NULL", "(qty IS NULL) IS NULL"],
        ["qty + 1 IS NULL", "qty + 1 IS NULL"],
        // COLLATE.
        ["name COLLATE case_insensitive = 'ABC'", "name COLLATE case_insensitive = 'ABC'"],
        ["name = 'abc' COLLATE BINARY", "name = 'abc' COLLATE binary"],
        ["(name || 'x') COLLATE case_accent_insensitive = 'y'", "(name || 'x') COLLATE case_accent_insensitive = 'y'"],
        // Builtin functions keep their spelling; arguments are comma-separated.
        ["UPPER(name) = 'A'", "UPPER(name) = 'A'"],
        ["lower(name) <> ''", "lower(name) <> ''"],
        ["LENGTH( name ) > 0", "LENGTH(name) > 0"],
        ["COALESCE(flag,FALSE)", "COALESCE(flag, FALSE)"],
        ["ABS(qty) < 100", "ABS(qty) < 100"],
        // Literals: booleans, NULL, numbers as written, strings with quotes and comment text.
        ["flag", "flag"],
        ["flag = true", "flag = TRUE"],
        ["flag IS NULL OR NULL", "flag IS NULL OR NULL"],
        ["price > 1.5e3", "price > 1.5e3"],
        ["price > .5", "price > .5"],
        ["qty <> 007", "qty <> 007"],
        ["name <> 'O''Brien'", "name <> 'O''Brien'"],
        ["name <> ''''", "name <> ''''"],
        ["name <> '--not a comment'", "name <> '--not a comment'"],
        ["name <> '/* nor this */'", "name <> '/* nor this */'"],
        ["name <> 'ünïcödé ✓'", "name <> 'ünïcödé ✓'"],
        // Identifiers: unicode words stay bare; quotes stay only where the dialect needs them.
        ["größe > 0", "größe > 0"],
        ["数量 > 0", "数量 > 0"],
        ["\"qty\" > 0", "qty > 0"],
        ["\"order\" > 0", "\"order\" > 0"],
        ["\"my qty\" > 0", "\"my qty\" > 0"],
        ["\"Count\" > 0", "\"Count\" > 0"],
        ["\"escape\" = 'x'", "\"escape\" = 'x'"],
        ["\"ét\" > 0", "\"ét\" > 0"],
        ["\"1st\" > 0", "\"1st\" > 0"],
        ["\"\" > 0", "\"\" > 0"],
        ["t.qty > 0", "t.qty > 0"],
        // Comments and layout never reach the stored text.
        ["qty /* lower bound */ > 0 -- inclusive?\n", "qty > 0"],
        ["\n\tqty\n>\n0\n", "qty > 0"],
    ];

    /// <summary>
    /// Every CHECK form renders to its canonical text, and that text parses back to the same
    /// tree and renders to itself.
    /// </summary>
    /// <param name="written">The predicate as a user writes it.</param>
    /// <param name="canonical">The canonical text the engine stores.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Renderer: every CHECK form renders canonically and parses back to the same tree")]
    [MemberData(nameof(CheckCorpus))]
    public void Render_CheckPredicate_ShouldRoundTripThroughCanonicalText(string written, string canonical)
    {
        // Arrange
        var declared = ParseCheck(written);

        // Act
        string rendered = SqlExpressionRenderer.Render(declared);
        var reloaded = ParseCheck(rendered);

        // Assert
        rendered.ShouldBe(canonical);
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(declared));
        SqlExpressionRenderer.Render(reloaded).ShouldBe(rendered);
    }

    /// <summary>
    /// Forms outside CHECK — parameters, CAST, calls, subqueries, EXISTS and the star — render
    /// to text that parses back to the same tree, so later persisted expressions inherit the
    /// invariant.
    /// </summary>
    /// <param name="written">An expression as written in a select list.</param>
    /// <param name="canonical">Its canonical text.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Renderer: forms outside CHECK parse back to the same tree")]
    [InlineData("@p + $1", "@p + $1")]
    [InlineData("+@p", "+@p")]
    [InlineData("CAST(qty AS varchar( 10 ))", "CAST(qty AS VARCHAR(10))")]
    [InlineData("cast(price as decimal(10,2)) * 2", "CAST(price AS DECIMAL(10, 2)) * 2")]
    [InlineData("COUNT(*)", "COUNT(*)")]
    [InlineData("\"select\"(1)", "\"select\"(1)")]
    [InlineData("\"CAST\"(1)", "\"CAST\"(1)")]
    [InlineData("my_function(a, 'b', NULL)", "my_function(a, 'b', NULL)")]
    [InlineData("(SELECT MAX(qty) FROM t) + 1", "(SELECT MAX(qty) FROM t) + 1")]
    [InlineData("EXISTS (SELECT 1 FROM t WHERE qty > 0)", "EXISTS (SELECT 1 FROM t WHERE qty > 0)")]
    [InlineData("NOT EXISTS (SELECT 1 FROM t)", "NOT EXISTS (SELECT 1 FROM t)")]
    [InlineData("NOT (EXISTS (SELECT 1 FROM t))", "NOT (EXISTS (SELECT 1 FROM t))")]
    [InlineData("NOT (EXISTS (SELECT 1 FROM t) = TRUE)", "NOT (EXISTS (SELECT 1 FROM t) = TRUE)")]
    [InlineData("(NOT EXISTS (SELECT 1 FROM t)) = FALSE", "(NOT EXISTS (SELECT 1 FROM t)) = FALSE")]
    [InlineData("qty IN (SELECT qty FROM u WHERE u.id > 0)", "qty IN (SELECT qty FROM u WHERE u.id > 0)")]
    [InlineData("qty NOT IN ((SELECT 1 FROM u))", "qty NOT IN ((SELECT 1 FROM u))")]
    [InlineData("~qty", "~qty")]
    [InlineData("\"rank\"(1)", "\"rank\"(1)")]
    [InlineData("\"row_number\"()", "\"row_number\"()")]
    [InlineData("\"rank\"(\"rank\") = 1", "\"rank\"(\"rank\") = 1")]
    [InlineData("f((*) = 1)", "f((* = 1))")]
    [InlineData("UPPER((*) || 'x', (*))", "UPPER((* || 'x'), *)")]
    [InlineData("f((*) = +@p)", "f((* = +@p))")]
    public void Render_GeneralExpression_ShouldRoundTrip(string written, string canonical)
    {
        // Arrange
        var declared = ParseSelectColumn(written, out _);

        // Act
        string rendered = SqlExpressionRenderer.Render(declared);
        var reloaded = ParseSelectColumn(rendered, out var diagnostics);

        // Assert
        rendered.ShouldBe(canonical);
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(declared));
        if (written != "~qty")
        {
            diagnostics.ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A delimited call to a user function named like a builtin window function stays delimited:
    /// the bare spelling parses as the window function, which the dialect rejects.
    /// </summary>
    /// <param name="name">A window function name.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Renderer: a call named like a window function stays delimited")]
    [InlineData("ROW_NUMBER")]
    [InlineData("rank")]
    [InlineData("Dense_Rank")]
    [InlineData("lead")]
    [InlineData("lag")]
    [InlineData("first_value")]
    [InlineData("last_value")]
    [InlineData("nth_value")]
    [InlineData("ntile")]
    public void Render_WindowFunctionNamedCall_ShouldStayDelimited(string name)
    {
        // Arrange
        var declared = ParseSelectColumn($"\"{name}\"(qty)", out var errors);
        errors.ShouldBeEmpty();

        // Act
        string rendered = SqlExpressionRenderer.Render(declared);
        var reloaded = ParseSelectColumn(rendered, out var reloadErrors);

        // Assert
        rendered.ShouldBe($"\"{name}\"(qty)");
        reloadErrors.ShouldBeEmpty();
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(declared));
    }

    /// <summary>A whole SELECT, the form a stored view will persist, renders and parses back to the same tree.</summary>
    /// <param name="written">The query as written.</param>
    /// <param name="canonical">Its canonical text.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Renderer: queries render canonically and parse back to the same tree")]
    [InlineData("select * from t", "SELECT * FROM t")]
    [InlineData("SELECT DISTINCT a AS x, b y FROM dbo.t AS q WHERE a > 0 ORDER BY a DESC, 2 LIMIT 5 OFFSET 1",
        "SELECT DISTINCT a AS x, b AS y FROM dbo.t AS q WHERE a > 0 ORDER BY a DESC, 2 LIMIT 5 OFFSET 1")]
    [InlineData("SELECT a, COUNT(*) FROM t GROUP BY a HAVING COUNT(*) > 1",
        "SELECT a, COUNT(*) FROM t GROUP BY a HAVING COUNT(*) > 1")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.id = u.id", "SELECT t.a FROM t INNER JOIN u ON t.id = u.id")]
    [InlineData("SELECT \"order\" FROM \"select\" AS \"from\"", "SELECT \"order\" FROM \"select\" AS \"from\"")]
    [InlineData("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS ORDER BY +1",
        "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS ORDER BY 1")]
    public void Render_Query_ShouldRoundTrip(string written, string canonical)
    {
        // Arrange
        var declared = ParseSelect(written);

        // Act
        string rendered = SqlExpressionRenderer.Render(declared);
        var reloaded = ParseSelect(rendered);

        // Assert
        rendered.ShouldBe(canonical);
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(declared));
    }

    /// <summary>
    /// Thousands of random expressions over the whole grammar, in select-list and predicate
    /// positions, keep their tree through render and reparse, and the canonical text is a fixed
    /// point. The corpus above names the forms; this finds the combinations nobody wrote down.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Renderer: random expressions keep their tree through canonical text")]
    public void Render_RandomExpressions_ShouldRoundTrip()
    {
        // Arrange
        var generator = new SqlExpressionTextGenerator(seed: 1068);
        int checkedCount = 0;

        for (int iteration = 0; iteration < 4000; iteration++)
        {
            string text = generator.Next(depth: 1 + iteration % 4);
            foreach (bool inPredicate in new[] { false, true })
            {
                var declared = inPredicate ? ParseWhere(text, out var errors) : ParseSelectColumn(text, out errors);
                if (declared is null || errors.Count > 0)
                {
                    continue; // the generator also writes text the dialect rejects
                }

                // Act
                string rendered = SqlExpressionRenderer.Render(declared);
                var reloaded = inPredicate ? ParseWhere(rendered, out var reloadErrors) : ParseSelectColumn(rendered, out reloadErrors);

                // Assert
                reloadErrors.ShouldBeEmpty($"'{text}' rendered as '{rendered}', which does not parse.");
                SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(declared), $"'{text}' rendered as '{rendered}'.");
                SqlExpressionRenderer.Render(reloaded!).ShouldBe(rendered, $"'{text}' is not a fixed point.");
                checkedCount++;
            }
        }

        // Most generated text must parse, or the property would hold vacuously.
        checkedCount.ShouldBeGreaterThan(3000);
    }

    /// <summary>A name the dialect cannot delimit, and a node it cannot spell, are refused rather than mis-rendered.</summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Renderer: names and nodes the dialect cannot spell are refused")]
    public void Render_Unspellable_ShouldThrowNotSupported()
    {
        // Arrange
        var quoted = new SqlColumnReferenceExpression("a\"b", null, null, null);
        var foreign = new ForeignExpression();

        // Act / Assert
        Should.Throw<NotSupportedException>(() => SqlExpressionRenderer.Render(quoted)).Message.ShouldContain("double quote");
        Should.Throw<NotSupportedException>(() => SqlExpressionRenderer.Render(foreign)).Message.ShouldContain(nameof(ForeignExpression));
        Should.Throw<ArgumentNullException>(() => SqlExpressionRenderer.Render((SqlExpression)null!));
        Should.Throw<ArgumentNullException>(() => SqlExpressionRenderer.Render((SqlSelectExpression)null!));
    }

    private static SqlExpression ParseCheck(string predicate)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"CREATE TABLE t (qty INT, CHECK ({predicate}));");
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty($"'{predicate}' must parse.");
        return statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Constraints.Single().CheckExpression.ShouldNotBeNull();
    }

    private static SqlExpression ParseSelectColumn(string expression, out List<Diagnostic> errors)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {expression} FROM t;");
        errors = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        return statement.SqlExpression is SqlSelectExpression { Columns.Count: 1 } select ? select.Columns[0].Expression : null!;
    }

    private static SqlExpression ParseWhere(string expression, out List<Diagnostic> errors)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT * FROM t WHERE {expression};");
        errors = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        return (statement.SqlExpression as SqlSelectExpression)?.Where!;
    }

    private static SqlSelectExpression ParseSelect(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty($"'{sql}' must parse.");
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
    }

    /// <summary>An expression node no parser produces.</summary>
    private sealed class ForeignExpression : SqlExpression
    {
        public ForeignExpression()
            : base(null)
        {
        }
    }
}
