using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// The expression nesting limit (#1151): an expression tree nests at most 128 levels, counted
/// by tree depth so that each link of a left-associative chain is a level, and parentheses nest
/// at most 128 deep. Deeper text reports <c>SQL0006</c> before anything executes, and no input
/// overflows the parser's stack.
/// </summary>
public sealed class SqlExpressionDepthTests
{
    private const int Limit = SqlQueryParser.MaximumExpressionDepth;

    /// <summary>The adversarial size from the #1068 review that overflowed the stack.</summary>
    private const int Hostile = 200_000;

    /// <summary>
    /// Builds an expression whose tree is exactly <c>depth</c> levels deep, one construct each.
    /// </summary>
    public static TheoryData<string> Constructs => new()
    {
        "additive", "multiplicative", "concatenation", "or", "and", "right-nested", "minus", "plus",
        "not", "case", "function", "cast", "in-list", "collate", "between",
    };

    private static string Build(string construct, int depth) => construct switch
    {
        // A chain of N terms is N levels deep: each operator is a node over the chain before it.
        "additive" => string.Join(" + ", Enumerable.Repeat("1", depth)),
        "multiplicative" => string.Join(" * ", Enumerable.Repeat("1", depth)),
        "concatenation" => string.Join(" || ", Enumerable.Repeat("'a'", depth)),
        "or" => string.Join(" OR ", Enumerable.Repeat("TRUE", depth)),
        "and" => string.Join(" AND ", Enumerable.Repeat("TRUE", depth)),
        // 1 + (1 + (... + 1)): N terms, N - 1 parentheses.
        "right-nested" => string.Concat(Enumerable.Repeat("1 + (", depth - 1)) + "1" + new string(')', depth - 1),
        // N - 1 signs over a leaf. The spaces keep two minus signs from starting a comment.
        "minus" => string.Concat(Enumerable.Repeat("- ", depth - 1)) + "a",
        "plus" => string.Concat(Enumerable.Repeat("+ ", depth - 1)) + "a",
        "not" => string.Concat(Enumerable.Repeat("NOT ", depth - 1)) + "TRUE",
        "case" => string.Concat(Enumerable.Repeat("CASE WHEN TRUE THEN ", depth - 1)) + "1" +
            string.Concat(Enumerable.Repeat(" END", depth - 1)),
        "function" => string.Concat(Enumerable.Repeat("ABS(", depth - 1)) + "1" + new string(')', depth - 1),
        "cast" => string.Concat(Enumerable.Repeat("CAST(", depth - 1)) + "1" +
            string.Concat(Enumerable.Repeat(" AS INT)", depth - 1)),
        "in-list" => string.Concat(Enumerable.Repeat("1 IN (", depth - 1)) + "1" + new string(')', depth - 1),
        "collate" => "'a'" + string.Concat(Enumerable.Repeat(" COLLATE binary", depth - 1)),
        "between" => string.Concat(Enumerable.Repeat("1 BETWEEN 0 AND (", depth - 1)) + "1" + new string(')', depth - 1),
        _ => throw new ArgumentOutOfRangeException(nameof(construct)),
    };

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: an expression exactly at the limit parses")]
    [MemberData(nameof(Constructs))]
    public void Parse_ExpressionAtLimit_ShouldParse(string construct)
    {
        // Arrange
        string expression = Build(construct, Limit);

        // Act
        var select = ParseSelect($"SELECT {expression} FROM t;");

        // Assert
        select.Columns.ShouldHaveSingleItem().Expression.Depth.ShouldBe(Limit);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: one level past the limit reports SQL0006")]
    [MemberData(nameof(Constructs))]
    public void Parse_ExpressionPastLimit_ShouldReportSql0006(string construct)
    {
        // Arrange
        string expression = Build(construct, Limit + 1);

        // Act
        var statement = Parse($"SELECT {expression} FROM t;");

        // Assert
        AssertRejected(statement, SqlQueryCommandType.Select);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a 200,000-level expression is rejected without overflowing the stack")]
    [MemberData(nameof(Constructs))]
    public void Parse_HostileExpression_ShouldReportSql0006(string construct)
    {
        // Arrange
        string expression = Build(construct, Hostile);
        var parser = new SqlQueryParser();

        // Act
        var statement = (SqlQueryStatement)parser.Parse($"SELECT id FROM t WHERE {expression} = 1;");

        // Assert
        AssertRejected(statement, SqlQueryCommandType.Select);
        Errors(parser.Parse("SELECT 1 + 1 FROM t;")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: parentheses nest at most 128 deep")]
    [InlineData(1, false)]
    [InlineData(Limit, false)]
    [InlineData(Limit + 1, true)]
    [InlineData(Hostile, true)]
    public void Parse_Parentheses_ShouldBeBounded(int pairs, bool rejected)
    {
        // Arrange
        string sql = "SELECT " + new string('(', pairs) + "1" + new string(')', pairs) + " FROM t;";

        // Act
        var statement = Parse(sql);

        // Assert
        if (rejected)
        {
            var error = AssertRejected(statement, SqlQueryCommandType.Select);
            error.Message.ShouldBe("Parentheses nest deeper than the supported limit of 128 levels.");
            error.Start.ShouldBe("SELECT ".Length + Limit);
        }
        else
        {
            Errors(statement).ShouldBeEmpty();
            statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression
                .ShouldBeOfType<SqlLiteralExpression>().Depth.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: parentheses are counted apart from the tree, each up to the limit")]
    public void Parse_ParenthesesAroundTreeAtLimit_ShouldParse()
    {
        // Arrange: 128 grouping parentheses around a 128-level chain.
        string sql = "SELECT " + new string('(', Limit) + Build("additive", Limit) + new string(')', Limit) + " FROM t;";

        // Act
        var select = ParseSelect(sql);

        // Assert
        select.Columns.Single().Expression.Depth.ShouldBe(Limit);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a chain is measured by tree depth and reported at the link that crosses the limit")]
    public void Parse_LeftAssociativeChain_ShouldReportAtCrossingOperator()
    {
        // Arrange: term i starts at 7 + 4(i - 1); the operator after term k at 7 + 4k - 2. The
        // node built at operator k is k + 1 levels deep, so operator 128 crosses the limit.
        string sql = $"SELECT {Build("additive", Limit + 1)} FROM t;";

        // Act
        var error = AssertRejected(Parse(sql), SqlQueryCommandType.Select);

        // Assert
        error.Message.ShouldBe("Expression nesting exceeds the supported limit of 128 levels.");
        error.Start.ShouldBe("SELECT ".Length + 4 * Limit - 2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: prefix operators are reported at the operand that would be one level too deep")]
    public void Parse_PrefixChain_ShouldReportAtTooDeepOperand()
    {
        // Arrange: 128 signs over a leaf make 129 levels; the leaf is the level too deep.
        string sql = "SELECT " + string.Concat(Enumerable.Repeat("- ", Limit)) + "a FROM t;";

        // Act
        var error = AssertRejected(Parse(sql), SqlQueryCommandType.Select);

        // Assert
        error.Start.ShouldBe("SELECT ".Length + 2 * Limit);
    }

    /// <summary>The examples DIALECT.md gives report SQL0006 at the token it names.</summary>
    /// <param name="example">The documented example.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: SQL0006 is reported where the dialect documents it")]
    [InlineData("128 nested calls")]
    [InlineData("128 comparisons under AND")]
    public void Parse_DocumentedExample_ShouldReportAtDocumentedToken(string example)
    {
        // Arrange
        bool calls = example == "128 nested calls";
        string expression = calls
            ? string.Concat(Enumerable.Repeat("ABS(", Limit)) + "1" + new string(')', Limit)
            : string.Join(" AND ", Enumerable.Range(1, Limit).Select(term => $"a = {term.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        string sql = $"SELECT id FROM t WHERE {expression};";

        // The innermost 1, or the 127th AND.
        int expected = sql.IndexOf('1', StringComparison.Ordinal);
        if (!calls)
        {
            expected = -1;
            for (int occurrence = 0; occurrence < Limit - 1; occurrence++)
            {
                expected = sql.IndexOf(" AND ", expected + 1, StringComparison.Ordinal);
            }
            expected++;
        }

        // Act
        var error = AssertRejected(Parse(sql), SqlQueryCommandType.Select);

        // Assert
        error.Start.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the limit spans subqueries, whose clauses nest below the subquery node")]
    [InlineData(Limit - 1, false)]
    [InlineData(Limit, true)]
    public void Parse_SubqueryClauses_ShouldCountBelowTheSubquery(int terms, bool rejected)
    {
        // Arrange: a scalar subquery is one level, its projection the rest.
        string sql = $"SELECT (SELECT {Build("additive", terms)} FROM u) FROM t;";

        // Act
        var statement = Parse(sql);

        // Assert
        if (rejected)
        {
            AssertRejected(statement, SqlQueryCommandType.Select);
        }
        else
        {
            Errors(statement).ShouldBeEmpty();
            var subquery = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression
                .ShouldBeOfType<SqlSubqueryExpression>();
            subquery.Depth.ShouldBe(Limit);
            subquery.Select.ExpressionDepth.ShouldBe(Limit - 1);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: every clause and statement kind enforces the limit")]
    [InlineData("SELECT id FROM t WHERE {0};", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM t GROUP BY {0};", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM t ORDER BY {0};", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM t INNER JOIN u ON {0};", SqlQueryCommandType.Select)]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY id HAVING {0};", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM t LIMIT {0};", SqlQueryCommandType.Select)]
    [InlineData("INSERT INTO t (id) VALUES ({0});", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO t (id) SELECT {0} FROM u;", SqlQueryCommandType.Insert)]
    [InlineData("UPDATE t SET id = {0} WHERE id = 1;", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM t WHERE {0};", SqlQueryCommandType.Delete)]
    [InlineData("CREATE TABLE t (id INT, CHECK ({0}));", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE t (id INT DEFAULT ({0}));", SqlQueryCommandType.Create)]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK ({0});", SqlQueryCommandType.Alter)]
    public void Parse_EveryClause_ShouldEnforceLimit(string template, SqlQueryCommandType command)
    {
        // Arrange
        string atLimit = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Build("additive", Limit));
        string pastLimit = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Build("additive", Limit + 1));

        // Act
        var accepted = Parse(atLimit);
        var rejected = Parse(pastLimit);

        // Assert
        Errors(accepted).ShouldNotContain(error => error.Code == "SQL0006");
        AssertRejected(rejected, command);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: diagnostics before the limit are kept and nothing after it is reported")]
    public void Parse_PastLimit_ShouldKeepEarlierDiagnosticsOnly()
    {
        // Arrange: ~ is reported before the limit; the unclosed parentheses and missing END after
        // it would each report SQL0003 if the abandoned rules were allowed to.
        string sql = $"SELECT ~a, (CASE WHEN {Build("or", Limit + 1)} THEN 1 FROM t WHRE (id = 1;";

        // Act
        var statement = Parse(sql);

        // Assert
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Code).ShouldBe(["COHDBL001", "SQL0006"]);
        statement.SqlExpression.ShouldNotBeOfType<SqlSelectExpression>();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a parser is reusable after a statement past the limit")]
    public void Parse_AfterRejectedStatement_ShouldParseNormally()
    {
        // Arrange
        var parser = new SqlQueryParser();
        parser.Parse("SELECT " + new string('(', Hostile) + "1" + new string(')', Hostile) + " FROM t;");

        // Act
        var statement = (SqlQueryStatement)parser.Parse($"SELECT {Build("minus", Limit)} FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.Depth.ShouldBe(Limit);
    }

    /// <summary>
    /// One of the deepest statements the limits accept, 128 parentheses around 128 levels of
    /// calls, each level a full pass down the precedence ladder: it parses on a thread with
    /// ample stack, and on one too small to recurse that far it is SQL0007, never an overflow.
    /// </summary>
    /// <param name="stackKilobytes">The thread's maximum stack size.</param>
    /// <param name="code">The error expected, or null when the statement parses.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a thread with too little stack gets SQL0007, never a stack overflow")]
    [InlineData(8 * 1024, null)]
    [InlineData(160, "SQL0007")]
    public void Parse_DeepestStatementByStackSize_ShouldParseOrReportSql0007(int stackKilobytes, string? code)
    {
        // Arrange
        string sql = "SELECT " + new string('(', Limit) + Build("function", Limit) + new string(')', Limit) + " FROM t;";
        SqlQueryStatement? statement = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                statement = Parse(sql);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, maxStackSize: stackKilobytes * 1024);

        // Act
        thread.Start();
        thread.Join();

        // Assert: the parser never throws.
        failure.ShouldBeNull();
        statement.ShouldNotBeNull();
        if (code is null)
        {
            Errors(statement).ShouldBeEmpty();
            return;
        }

        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe(code);
        error.Message.ShouldBe("Expression nesting exceeds the stack available to the parser on this thread.");
        statement.SqlExpression.GetType().ShouldBe(typeof(SqlQueryExpression));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: depth counts nodes, not parentheses")]
    public void Depth_ShouldCountNodes()
    {
        // Act
        var columns = ParseSelect(
            "SELECT 1, ((a)), (a + b) * c, -a, CASE WHEN a = 1 THEN b + c END, COUNT(*), " +
            "a IN (1, 2 + 3), EXISTS (SELECT x + 1 FROM u) FROM t;").Columns;

        // Assert
        columns.Select(column => column.Expression.Depth).ShouldBe([1, 1, 3, 2, 3, 2, 3, 3]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the canonical text of a CHECK at the limit parses back to the same tree")]
    public void Render_CheckAtLimit_ShouldParseBack()
    {
        // Arrange: a sign applied to a sign is rendered with parentheses, so the canonical text
        // of this predicate adds 125 pairs; it still parses under both limits.
        string predicate = string.Concat(Enumerable.Repeat("- ", Limit - 2)) + "a > 0";
        var check = ParseCheck(predicate);

        // Act
        string canonical = SqlExpressionRenderer.Render(check);
        var reloaded = ParseCheck(canonical);

        // Assert
        check.Depth.ShouldBe(Limit);
        canonical.ShouldStartWith("-(-(-(");
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(check));
        SqlExpressionRenderer.Render(reloaded).ShouldBe(canonical);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the renderer refuses a hand-built tree deeper than the limit")]
    [InlineData(Limit + 1)]
    [InlineData(Hostile)]
    public void Render_TreePastLimit_ShouldThrowNotSupported(int depth)
    {
        // Arrange: the parser cannot produce this tree; the internal constructors can.
        SqlExpression tree = new SqlLiteralExpression("1", SqlLiteralType.Integer, null);
        for (int level = 1; level < depth; level++)
        {
            tree = new SqlBinaryExpression(tree, SqlBinaryOperator.Add,
                new SqlLiteralExpression("1", SqlLiteralType.Integer, null), null);
        }
        var query = new SqlSelectExpression([new SqlSelectColumn(tree, null)], new SqlTableReference("t", null, null),
            [], null, [], null, [], null, null, false, null, null);

        // Act
        var expression = Should.Throw<NotSupportedException>(() => SqlExpressionRenderer.Render(tree));
        var select = Should.Throw<NotSupportedException>(() => SqlExpressionRenderer.Render(query));

        // Assert
        tree.Depth.ShouldBe(depth);
        expression.Message.ShouldContain($"nests {depth} levels deep");
        select.Message.ShouldContain($"nests {depth} levels deep");
    }

    private static Diagnostic AssertRejected(SqlQueryStatement statement, SqlQueryCommandType command)
    {
        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0006");
        error.Severity.ShouldBe(DiagnosticSeverity.Error);

        // Nothing past the limit was parsed, so only the command type is kept.
        statement.SqlExpression.GetType().ShouldBe(typeof(SqlQueryExpression));
        statement.SqlExpression.CommandType.ShouldBe(command);
        return error;
    }

    private static SqlExpression ParseCheck(string predicate)
    {
        var statement = Parse($"CREATE TABLE t (a INT, CHECK ({predicate}));");
        Errors(statement).ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Constraints.Single().CheckExpression.ShouldNotBeNull();
    }

    private static SqlSelectExpression ParseSelect(string sql)
    {
        var statement = Parse(sql);
        Errors(statement).ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    private static List<Diagnostic> Errors(QueryStatement statement)
        => statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
}
