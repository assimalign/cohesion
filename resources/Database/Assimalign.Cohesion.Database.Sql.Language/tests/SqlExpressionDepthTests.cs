using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// The expression nesting limit (#1151, owner decision of 2026-10-01: a happy medium between SQL
/// Server and PostgreSQL). Only genuine nesting counts: an expression tree nests at most the
/// configured limit (256 by default, 32..4096), counted by tree depth so that each link of an
/// arithmetic chain is a level while a whole <c>AND</c>/<c>OR</c> chain is one, and parentheses
/// nest at most as deep. Deeper text reports <c>SQL0006</c> before anything executes, and no input
/// overflows the parser's stack.
/// </summary>
public sealed class SqlExpressionDepthTests
{
    private const int Limit = SqlQueryParserOptions.DefaultExpressionNestingLimit;

    /// <summary>The adversarial size from the #1068 review that overflowed the stack.</summary>
    private const int Hostile = 200_000;

    /// <summary>
    /// Builds an expression whose tree is exactly <c>depth</c> levels deep, one construct each:
    /// every one of them is genuine nesting.
    /// </summary>
    public static TheoryData<string> Constructs => new()
    {
        "additive", "multiplicative", "concatenation", "right-nested", "and-right-nested", "or-and-alternating",
        "minus", "plus", "not", "case", "function", "cast", "in-list", "collate", "between",
    };

    private static string Build(string construct, int depth) => construct switch
    {
        // A chain of N terms is N levels deep: each operator is a node over the chain before it.
        "additive" => string.Join(" + ", Enumerable.Repeat("1", depth)),
        "multiplicative" => string.Join(" * ", Enumerable.Repeat("1", depth)),
        "concatenation" => string.Join(" || ", Enumerable.Repeat("'a'", depth)),
        // 1 + (1 + (... + 1)): N terms, N - 1 parentheses.
        "right-nested" => string.Concat(Enumerable.Repeat("1 + (", depth - 1)) + "1" + new string(')', depth - 1),
        // TRUE AND (TRUE AND (...)): a chain nested in a later position is a nested node, N - 1
        // chains over N leaves.
        "and-right-nested" => string.Concat(Enumerable.Repeat("TRUE AND (", depth - 1)) + "TRUE" + new string(')', depth - 1),
        "or-and-alternating" => string.Concat(Enumerable.Range(0, depth - 1).Select(level => level % 2 == 0 ? "TRUE OR (" : "TRUE AND (")) +
            "TRUE" + new string(')', depth - 1),
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

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: an expression exactly at the default limit parses")]
    [MemberData(nameof(Constructs))]
    public void Parse_ExpressionAtLimit_ShouldParse(string construct)
    {
        // Arrange
        string expression = Build(construct, Limit);

        // Act
        var statement = Parse($"SELECT {expression} FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.ShouldHaveSingleItem().Expression.Depth.ShouldBe(Limit);
        statement.ExpressionNestingDepth.ShouldBe(Limit);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: one level past the default limit reports SQL0006")]
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
        var (statement, next) = OnLargeStack(() =>
            ((SqlQueryStatement)parser.Parse($"SELECT id FROM t WHERE {expression} = 1;"), parser.Parse("SELECT 1 + 1 FROM t;")));

        // Assert
        AssertRejected(statement, SqlQueryCommandType.Select);
        Errors(next).ShouldBeEmpty();
    }

    /// <summary>
    /// A chain of AND (or OR) terms is one n-ary node, PostgreSQL's shape: it costs one level of
    /// the limit however long it is, so 10,000 and the hostile 200,000 terms both parse.
    /// </summary>
    /// <param name="keyword">The chain's operator.</param>
    /// <param name="terms">How many comparisons the chain joins.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: an AND or OR chain of any length is one level")]
    [InlineData("AND", 2)]
    [InlineData("AND", Limit + 1)]
    [InlineData("AND", 10_000)]
    [InlineData("OR", 10_000)]
    [InlineData("OR", Hostile)]
    public void Parse_FlatLogicalChain_ShouldBeOneLevel(string keyword, int terms)
    {
        // Arrange
        string predicate = string.Join($" {keyword} ", Enumerable.Range(1, terms).Select(term => $"id = {Number(term)}"));

        // Act
        var statement = Parse($"SELECT id FROM t WHERE {predicate};");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var chain = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlLogicalExpression>();
        chain.Operator.ShouldBe(keyword == "AND" ? SqlLogicalOperator.And : SqlLogicalOperator.Or);
        chain.Operands.Count.ShouldBe(terms);
        chain.Operands.ShouldAllBe(operand => operand is SqlBinaryExpression);
        chain.Operands[^1].ShouldBeOfType<SqlBinaryExpression>().Right.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe(Number(terms));
        chain.Depth.ShouldBe(3);
        statement.ExpressionNestingDepth.ShouldBe(3);
    }

    /// <summary>
    /// The n-ary tree is the binary one with each run of links collapsed: precedence is unchanged,
    /// a parenthesized chain of the same operator merges only when it opens the chain (as left
    /// associativity read it), and one in a later position stays a nested node.
    /// </summary>
    /// <param name="predicate">The predicate as written.</param>
    /// <param name="expected">Its tree.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: AND/OR chains keep precedence and left associativity")]
    [InlineData("a AND b AND c", "Logical(And, [a, b, c])")]
    [InlineData("(a AND b) AND c", "Logical(And, [a, b, c])")]
    [InlineData("((a AND b) AND c) AND d", "Logical(And, [a, b, c, d])")]
    [InlineData("a AND (b AND c)", "Logical(And, [a, Logical(And, [b, c])])")]
    [InlineData("(a AND b) AND (c AND d)", "Logical(And, [a, b, Logical(And, [c, d])])")]
    [InlineData("a OR b AND c OR d", "Logical(Or, [a, Logical(And, [b, c]), d])")]
    [InlineData("(a OR b) AND c", "Logical(And, [Logical(Or, [a, b]), c])")]
    [InlineData("(a OR b) OR c AND d", "Logical(Or, [a, b, Logical(And, [c, d])])")]
    [InlineData("NOT (a AND b) AND c", "Logical(And, [Not(Logical(And, [a, b])), c])")]
    [InlineData("a AND b OR c AND d", "Logical(Or, [Logical(And, [a, b]), Logical(And, [c, d])])")]
    [InlineData("a BETWEEN 1 AND 2 AND b", "Logical(And, [Between(a), b])")]
    public void Parse_LogicalChain_ShouldKeepPrecedenceAndAssociativity(string predicate, string expected)
    {
        // Act
        var where = Parse($"SELECT id FROM t WHERE {predicate};").SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where;

        // Assert
        Shape(where).ShouldBe(expected);
    }

    /// <summary>
    /// A chain that absorbs a parenthesized chain tracks its deepest operand as it goes rather
    /// than rescanning its operands, so its depth is exactly the depth of the tree it holds.
    /// </summary>
    /// <param name="predicate">The predicate as written.</param>
    /// <param name="depth">The depth of its tree.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a merged chain's depth is the depth of its deepest operand plus one")]
    [InlineData("(a AND b) AND c", 2)]
    [InlineData("(a AND (b OR c)) AND d", 3)]
    [InlineData("((a AND (b OR (c AND (d OR e)))) AND f) AND g", 5)]
    [InlineData("(a OR b) OR (c AND (d OR e)) OR f", 4)]
    [InlineData("((a AND b) AND NOT NOT NOT c) AND d", 5)]
    [InlineData("((a AND b) AND c) AND (d OR (e AND f))", 4)]
    public void Parse_MergedChain_ShouldTrackItsDepth(string predicate, int depth)
    {
        // Act
        var where = Parse($"SELECT id FROM t WHERE {predicate};").SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldNotBeNull();

        // Assert
        where.Depth.ShouldBe(depth);
        where.Depth.ShouldBe(TreeDepth(where));
    }

    /// <summary>
    /// A chain absorbs the parenthesized chain that opens it by taking over its operand list, so a
    /// long chain wrapped in parentheses level after level costs what the same chain written flat
    /// costs. Copying the list at every level made <c>((X AND t) AND t) ... AND t</c> cost the
    /// chain's length times its parentheses, in time and in memory: gigabytes for a statement of a
    /// few megabytes, which any wire client could send.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: merging parenthesized chains costs what the flat chain costs")]
    public void Parse_NestedChainMerges_ShouldCostWhatTheFlatChainCosts()
    {
        // Arrange: a 20,000-term chain under 4,095 left-nested parentheses, each adding a term.
        const int Terms = 20_000;
        const int Levels = SqlQueryParserOptions.MaximumExpressionNestingLimit - 1;
        string chain = string.Join(" AND ", Enumerable.Range(1, Terms).Select(term => $"a = {Number(term)}"));
        string added = string.Concat(Enumerable.Range(1, Levels).Select(level => $" AND b = {Number(level)}"));
        string flat = $"SELECT id FROM t WHERE {chain}{added};";
        string nested = "SELECT id FROM t WHERE " + new string('(', Levels) + chain +
            string.Concat(Enumerable.Range(1, Levels).Select(level => $") AND b = {Number(level)}")) + ";";
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit });

        // Act: each parse is measured on the thread that runs it, after a parse that warms up both
        // shapes. Each level of parentheses runs the whole precedence ladder, about 6 KB of stack
        // in a debug build.
        var (measured, failure) = RunOnThread(64 * 1024, () =>
        {
            parser.Parse("SELECT id FROM t WHERE ((a = 1 AND a = 2) AND b = 1) AND b = 2;");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var flatStatement = (SqlQueryStatement)parser.Parse(flat);
            long flatBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var nestedStatement = (SqlQueryStatement)parser.Parse(nested);
            long nestedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return (Flat: flatStatement, FlatBytes: flatBytes, Nested: nestedStatement, NestedBytes: nestedBytes);
        });

        // Assert
        failure.ShouldBeNull();
        Errors(measured.Flat).ShouldBeEmpty();
        Errors(measured.Nested).ShouldBeEmpty();
        var flatWhere = measured.Flat.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlLogicalExpression>();
        var nestedWhere = measured.Nested.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlLogicalExpression>();
        nestedWhere.Operands.Count.ShouldBe(Terms + Levels);
        nestedWhere.Depth.ShouldBe(3);
        measured.Nested.ExpressionNestingDepth.ShouldBe(Levels);
        SqlExpressionRenderer.Render(nestedWhere).ShouldBe(SqlExpressionRenderer.Render(flatWhere));

        // The nested text adds 8,190 parentheses and nothing else; the copies added gigabytes.
        measured.NestedBytes.ShouldBeLessThan(measured.FlatBytes * 3 / 2);
    }

    /// <summary>
    /// A subquery taken out of a parsed statement to run on its own carries no parser's measure.
    /// It is never parsed again, so it measures the depth of its own expression tree, which every
    /// walk over it recurses through: an engine's limit holds for it as for the statement it came
    /// from.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a subquery taken out of a statement measures its own tree")]
    public void ExpressionNestingDepth_ExtractedQuery_ShouldMeasureItsOwnTree()
    {
        // Arrange: each nested query filters on 300 levels of NOT, 301 with its leaf.
        string deep = string.Concat(Enumerable.Repeat("NOT ", 300)) + "TRUE";
        var select = Parse(
            $"SELECT (SELECT x FROM u WHERE {deep}) FROM t " +
            $"WHERE id IN (SELECT x FROM u WHERE {deep}) AND EXISTS (SELECT x FROM u WHERE {deep});",
            SqlQueryParserOptions.MaximumExpressionNestingLimit);
        var insert = Parse($"INSERT INTO t (id) SELECT x FROM u WHERE {deep};", SqlQueryParserOptions.MaximumExpressionNestingLimit);
        Errors(select).ShouldBeEmpty();
        Errors(insert).ShouldBeEmpty();
        var outer = select.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        var where = outer.Where.ShouldBeOfType<SqlLogicalExpression>();

        // Act
        var queries = new[]
        {
            outer.Columns.Single().Expression.ShouldBeOfType<SqlSubqueryExpression>().Select,
            where.Operands[0].ShouldBeOfType<SqlInExpression>().Subquery.ShouldNotBeNull(),
            where.Operands[1].ShouldBeOfType<SqlExistsExpression>().Subquery,
            insert.SqlExpression.ShouldBeOfType<SqlInsertExpression>().SelectSource.ShouldNotBeNull(),
        }.Select(query => new SqlQueryStatement(query).ExpressionNestingDepth);

        // Assert: the IN and EXISTS nodes are a level over their queries, the chain one more.
        queries.ShouldBe([301, 301, 301, 301]);
        select.ExpressionNestingDepth.ShouldBe(303);
        new SqlQueryStatement(outer).ExpressionNestingDepth.ShouldBe(303);
        insert.ExpressionNestingDepth.ShouldBe(301);
        new SqlQueryStatement(new SqlQueryExpression(SqlQueryCommandType.Select, null, null)).ExpressionNestingDepth.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: parentheses nest at most as deep as the limit")]
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
            error.Message.ShouldBe("Parentheses nest deeper than the supported limit of 256 levels.");
            error.Start.ShouldBe("SELECT ".Length + Limit);
        }
        else
        {
            Errors(statement).ShouldBeEmpty();
            statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression
                .ShouldBeOfType<SqlLiteralExpression>().Depth.ShouldBe(1);
            statement.ExpressionNestingDepth.ShouldBe(pairs);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: parentheses are counted apart from the tree, each up to the limit")]
    public void Parse_ParenthesesAroundTreeAtLimit_ShouldParse()
    {
        // Arrange: 256 grouping parentheses around a 256-level chain.
        string sql = "SELECT " + new string('(', Limit) + Build("additive", Limit) + new string(')', Limit) + " FROM t;";

        // Act
        var statement = Parse(sql);

        // Assert
        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.Depth.ShouldBe(Limit);
        statement.ExpressionNestingDepth.ShouldBe(Limit);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a chain is measured by tree depth and reported at the link that crosses the limit")]
    public void Parse_LeftAssociativeChain_ShouldReportAtCrossingOperator()
    {
        // Arrange: term i starts at 7 + 4(i - 1); the operator after term k at 7 + 4k - 2. The
        // node built at operator k is k + 1 levels deep, so operator 256 crosses the limit.
        string sql = $"SELECT {Build("additive", Limit + 1)} FROM t;";

        // Act
        var error = AssertRejected(Parse(sql), SqlQueryCommandType.Select);

        // Assert
        error.Message.ShouldBe("Expression nesting exceeds the supported limit of 256 levels.");
        error.Start.ShouldBe("SELECT ".Length + 4 * Limit - 2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: prefix operators are reported at the operand that would be one level too deep")]
    public void Parse_PrefixChain_ShouldReportAtTooDeepOperand()
    {
        // Arrange: 256 signs over a leaf make 257 levels; the leaf is the level too deep.
        string sql = "SELECT " + string.Concat(Enumerable.Repeat("- ", Limit)) + "a FROM t;";

        // Act
        var error = AssertRejected(Parse(sql), SqlQueryCommandType.Select);

        // Assert
        error.Start.ShouldBe("SELECT ".Length + 2 * Limit);
    }

    /// <summary>The examples DIALECT.md gives report SQL0006 at the token it names.</summary>
    /// <param name="example">The documented example.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: SQL0006 is reported where the dialect documents it")]
    [InlineData("256 nested calls")]
    [InlineData("256 comparisons in right-nested OR groups")]
    public void Parse_DocumentedExample_ShouldReportAtDocumentedToken(string example)
    {
        // Arrange
        bool calls = example == "256 nested calls";
        string expression = calls
            ? string.Concat(Enumerable.Repeat("ABS(", Limit)) + "1" + new string(')', Limit)
            : string.Concat(Enumerable.Range(1, Limit - 1).Select(term => $"a = {Number(term)} OR (")) +
                $"a = {Number(Limit)}" + new string(')', Limit - 1);
        string sql = $"SELECT id FROM t WHERE {expression};";

        // The innermost 1, or the 255th OR.
        int expected = sql.IndexOf("(1)", StringComparison.Ordinal) + 1;
        if (!calls)
        {
            expected = -1;
            for (int occurrence = 0; occurrence < Limit - 1; occurrence++)
            {
                expected = sql.IndexOf(" OR ", expected + 1, StringComparison.Ordinal);
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
            statement.ExpressionNestingDepth.ShouldBe(Limit);
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
        string atLimit = string.Format(CultureInfo.InvariantCulture, template, Build("additive", Limit));
        string pastLimit = string.Format(CultureInfo.InvariantCulture, template, Build("additive", Limit + 1));

        // Act
        var accepted = Parse(atLimit);
        var rejected = Parse(pastLimit);

        // Assert
        Errors(accepted).ShouldNotContain(error => error.Code == "SQL0006");
        accepted.ExpressionNestingDepth.ShouldBe(Limit);
        AssertRejected(rejected, command);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: diagnostics before the limit are kept and nothing after it is reported")]
    public void Parse_PastLimit_ShouldKeepEarlierDiagnosticsOnly()
    {
        // Arrange: ~ is reported before the limit; the unclosed parentheses and missing END after
        // it would each report SQL0003 if the abandoned rules were allowed to.
        string sql = $"SELECT ~a, (CASE WHEN {Build("additive", Limit + 1)} THEN 1 FROM t WHRE (id = 1;";

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
        var rejected = OnLargeStack(() => (SqlQueryStatement)parser.Parse("SELECT " + new string('(', Hostile) + "1" + new string(')', Hostile) + " FROM t;"));

        // Act
        var statement = OnLargeStack(() => (SqlQueryStatement)parser.Parse($"SELECT {Build("minus", Limit)} FROM t;"));

        // Assert
        AssertRejected(rejected, SqlQueryCommandType.Select);
        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.Depth.ShouldBe(Limit);
        statement.ExpressionNestingDepth.ShouldBe(Limit);
    }

    /// <summary>
    /// One of the deepest statements the default limit accepts, 256 parentheses around 256 levels
    /// of calls, each level a full pass down the precedence ladder: it parses on a thread with
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

        // Act: parsed on the sized thread itself, not through the large-stack helper.
        var (statement, failure) = RunOnThread(stackKilobytes, () => (SqlQueryStatement)new SqlQueryParser().Parse(sql));

        // Assert: the parser never throws.
        failure.ShouldBeNull();
        statement.ShouldNotBeNull();
        if (code is null)
        {
            Errors(statement).ShouldBeEmpty();
            statement.ExpressionNestingDepth.ShouldBe(Limit);
            return;
        }

        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe(code);
        error.Message.ShouldBe("Expression nesting exceeds the stack available to the parser on this thread.");
        statement.SqlExpression.GetType().ShouldBe(typeof(SqlQueryExpression));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: depth counts nodes, not parentheses or chain terms")]
    public void Depth_ShouldCountNodes()
    {
        // Act
        var columns = ParseSelect(
            "SELECT 1, ((a)), (a + b) * c, -a, CASE WHEN a = 1 THEN b + c END, COUNT(*), " +
            "a IN (1, 2 + 3), EXISTS (SELECT x + 1 FROM u), a AND b AND c AND d, " +
            "a = 1 OR b = 2 OR c = 3, a AND (b OR c) FROM t;").Columns;

        // Assert
        columns.Select(column => column.Expression.Depth).ShouldBe([1, 1, 3, 2, 3, 2, 3, 3, 2, 3, 3]);
    }

    /// <summary>
    /// The star of <c>COUNT(*)</c> is the call's operand, a level below it like any other
    /// argument. It used to skip the check, so a call at the deepest level built a tree one level
    /// deeper than the limit.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the COUNT(*) star is a level below its call")]
    [InlineData(Limit - 2, false)]
    [InlineData(Limit - 1, true)]
    public void Parse_CountStarAtLimit_ShouldCountTheStar(int calls, bool rejected)
    {
        // Arrange: the calls enclose COUNT, which encloses the star.
        string sql = "SELECT " + string.Concat(Enumerable.Repeat("ABS(", calls)) + "COUNT(*)" + new string(')', calls) + " FROM t;";

        // Act
        var statement = Parse(sql);

        // Assert
        if (rejected)
        {
            AssertRejected(statement, SqlQueryCommandType.Select).Start.ShouldBe(sql.IndexOf('*', StringComparison.Ordinal));
            return;
        }

        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression.Depth.ShouldBe(Limit);
        statement.ExpressionNestingDepth.ShouldBe(Limit);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the canonical text of a CHECK at the limit parses back to the same tree")]
    public void Render_CheckAtLimit_ShouldParseBack()
    {
        // Arrange: a sign applied to a sign is rendered with parentheses, so the canonical text
        // of this predicate adds 253 pairs; it still parses under both limits.
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

    /// <summary>
    /// The canonical text of a long chain is the text the binary form rendered, term after term,
    /// and it parses back to the same n-ary tree: stored definitions round-trip.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the canonical text of a 10,000-term CHECK parses back to the same tree")]
    public void Render_LongChainCheck_ShouldRoundTrip()
    {
        // Arrange: OR groups under one AND chain, and a nested chain in a later position.
        string predicate = string.Join(" AND ", Enumerable.Range(1, 10_000).Select(term => term % 1000 == 0
            ? $"(a = {Number(term)} OR a > {Number(term)})"
            : $"a <> {Number(term)}")) + " AND (a > 0 AND a < 100000)";
        var check = ParseCheck(predicate);

        // Act
        string canonical = SqlExpressionRenderer.Render(check);
        var reloaded = ParseCheck(canonical);

        // Assert
        check.ShouldBeOfType<SqlLogicalExpression>().Operands.Count.ShouldBe(10_001);
        canonical.ShouldStartWith("a <> 1 AND a <> 2 AND ", Case.Sensitive);
        canonical.ShouldContain(" AND (a = 1000 OR a > 1000) AND ", Case.Sensitive);
        canonical.ShouldEndWith(" AND (a > 0 AND a < 100000)", Case.Sensitive);
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(check));
        SqlExpressionRenderer.Render(reloaded).ShouldBe(canonical);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the renderer refuses a hand-built tree deeper than any parser reads")]
    [InlineData(SqlQueryParserOptions.MaximumExpressionNestingLimit + 1)]
    [InlineData(Hostile)]
    public void Render_TreePastCeiling_ShouldThrowNotSupported(int depth)
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

    /// <summary>
    /// The limit is configurable within 32..4096. Each configured value is exact, for the tree and
    /// for parentheses, and its diagnostic names it.
    /// </summary>
    /// <param name="limit">The configured limit.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a configured limit is enforced exactly")]
    [InlineData(SqlQueryParserOptions.MinimumExpressionNestingLimit)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(SqlQueryParserOptions.MaximumExpressionNestingLimit)]
    public void Parse_ConfiguredLimit_ShouldBeEnforcedExactly(int limit)
    {
        // Arrange: a left-associative chain is built in a loop, so even the highest limit parses
        // on an ordinary thread.
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = limit });

        // Act
        var atLimit = (SqlQueryStatement)parser.Parse($"SELECT {Build("additive", limit)} FROM t;");
        var pastLimit = (SqlQueryStatement)parser.Parse($"SELECT {Build("additive", limit + 1)} FROM t;");
        var flat = (SqlQueryStatement)parser.Parse($"SELECT id FROM t WHERE {string.Join(" OR ", Enumerable.Repeat("id = 1", limit + 1))};");

        // Assert
        Errors(atLimit).ShouldBeEmpty();
        atLimit.ExpressionNestingDepth.ShouldBe(limit);
        AssertRejected(pastLimit, SqlQueryCommandType.Select).Message
            .ShouldBe($"Expression nesting exceeds the supported limit of {Number(limit)} levels.");
        Errors(flat).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a configured limit bounds parentheses too")]
    [InlineData(SqlQueryParserOptions.MinimumExpressionNestingLimit)]
    [InlineData(500)]
    public void Parse_ConfiguredLimit_ShouldBoundParentheses(int limit)
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = limit });
        string Parenthesized(int pairs) => "SELECT " + new string('(', pairs) + "1" + new string(')', pairs) + " FROM t;";

        // Act
        var (atLimit, pastLimit) = RunOnThread(8 * 1024, () =>
            ((SqlQueryStatement)parser.Parse(Parenthesized(limit)), (SqlQueryStatement)parser.Parse(Parenthesized(limit + 1)))).Result;

        // Assert
        Errors(atLimit).ShouldBeEmpty();
        atLimit.ExpressionNestingDepth.ShouldBe(limit);
        AssertRejected(pastLimit, SqlQueryCommandType.Select).Message
            .ShouldBe($"Parentheses nest deeper than the supported limit of {Number(limit)} levels.");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a limit outside 32..4096 is refused when the parser is created")]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(SqlQueryParserOptions.MinimumExpressionNestingLimit - 1)]
    [InlineData(SqlQueryParserOptions.MaximumExpressionNestingLimit + 1)]
    [InlineData(int.MaxValue)]
    public void Constructor_LimitOutOfRange_ShouldThrow(int limit)
    {
        // Act
        var failure = Should.Throw<ArgumentOutOfRangeException>(() =>
            new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = limit }));

        // Assert
        failure.ParamName.ShouldBe("options");
        failure.ActualValue.ShouldBe(limit);
        failure.Message.ShouldStartWith("The expression nesting limit must be between 32 and 4096 levels.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the default limit is 256, also for options that set none")]
    public void Constructor_WithoutSqlOptions_ShouldApplyDefaultLimit()
    {
        // Arrange
        var parsers = new[]
        {
            new SqlQueryParser(),
            new SqlQueryParser(new QueryParserOptions()),
            new SqlQueryParser(new SqlQueryParserOptions()),
        };

        // Act + Assert
        new SqlQueryParserOptions().ExpressionNestingLimit.ShouldBe(256);
        foreach (var parser in parsers)
        {
            parser.ExpressionNestingLimit.ShouldBe(Limit);
            Errors(parser.Parse($"SELECT {Build("additive", Limit)} FROM t;")).ShouldBeEmpty();
            AssertRejected((SqlQueryStatement)parser.Parse($"SELECT {Build("additive", Limit + 1)} FROM t;"), SqlQueryCommandType.Select);
        }
    }

    /// <summary>
    /// The nesting the parser records on a statement is exactly what a limit decides: a parser
    /// with that limit accepts the statement and one with a limit a level lower refuses it. That
    /// is what lets an engine with a lower limit refuse a typed request another parser accepted,
    /// without parsing it again.
    /// </summary>
    /// <param name="template">A statement whose expression is <c>{0}</c>.</param>
    /// <param name="construct">The construct that nests.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Nesting: the recorded nesting is exactly the limit a statement needs")]
    [InlineData("SELECT {0} FROM t;", "function")]
    [InlineData("SELECT {0} FROM t;", "and-right-nested")]
    [InlineData("SELECT {0} FROM t;", "case")]
    [InlineData("SELECT id FROM t WHERE id = 1 AND ({0}) = 2;", "additive")]
    [InlineData("SELECT (SELECT {0} FROM u) FROM t;", "minus")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT x FROM u WHERE x = {0});", "collate")]
    [InlineData("SELECT ABS(((((({0})))))) FROM t;", "cast")]
    // 120 grouping parentheses, deeper than the tree at every size measured.
    [InlineData("SELECT {1}1{2} + {0} FROM t;", "in-list")]
    [InlineData("INSERT INTO t (id) VALUES (1), ({0});", "between")]
    [InlineData("UPDATE t SET id = 1 WHERE {0};", "not")]
    [InlineData("DELETE FROM t WHERE id = {0};", "right-nested")]
    [InlineData("CREATE TABLE t (id INT, CHECK (id > {0}));", "multiplicative")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK ({0} = 'a');", "concatenation")]
    public void ExpressionNestingDepth_ShouldBeTheLimitTheStatementNeeds(string template, string construct)
    {
        foreach (int depth in new[] { 40, 41, 100 })
        {
            // Arrange
            string sql = string.Format(CultureInfo.InvariantCulture, template, Build(construct, depth), new string('(', 120), new string(')', 120));
            var measured = Parse(sql, SqlQueryParserOptions.MaximumExpressionNestingLimit);
            Errors(measured).ShouldBeEmpty();
            int needed = measured.ExpressionNestingDepth;

            // Act
            var atNeeded = Parse(sql, needed);
            var belowNeeded = Parse(sql, needed - 1);

            // Assert
            needed.ShouldBeGreaterThanOrEqualTo(template.Contains("{1}", StringComparison.Ordinal) ? 120 : depth);
            Errors(atNeeded).ShouldBeEmpty();
            atNeeded.ExpressionNestingDepth.ShouldBe(needed);
            Errors(belowNeeded).ShouldContain(error => error.Code == "SQL0006");
        }
    }

    /// <summary>
    /// The correlation check over a subquery's clauses iterates, so its stack never depends on
    /// the tree: a chain thousands of levels deep, which the parser builds in a loop, is checked
    /// on a thread with little stack, and references are still reported in source order.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Nesting: a subquery's correlation check iterates over deep and long expressions")]
    public void Parse_DeepSubqueryUnderHighLimit_ShouldCheckCorrelationIteratively()
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit });
        string deep = $"SELECT (SELECT {Build("additive", 4000)} + x FROM u) FROM t;";
        string correlated = "SELECT id FROM t WHERE id IN (SELECT x FROM u WHERE " +
            string.Join(" AND ", Enumerable.Range(1, 10_000).Select(term => term is 10 or 9_000 ? $"t.c{Number(term)} = x" : $"u.x <> {Number(term)}")) + ");";

        // Act
        var (statements, failure) = RunOnThread(512, () =>
            ((SqlQueryStatement)parser.Parse(deep), (SqlQueryStatement)parser.Parse(correlated)));

        // Assert
        failure.ShouldBeNull();
        Errors(statements.Item1).ShouldBeEmpty();
        statements.Item1.ExpressionNestingDepth.ShouldBe(4002);
        Errors(statements.Item2).Where(error => error.Code == "COHDBL001").Select(error => error.Message).ShouldBe(
        [
            "Correlated SQL subqueries are not supported: reference 't.c10' is outside the subquery's local FROM/JOIN scope.",
            "Correlated SQL subqueries are not supported: reference 't.c9000' is outside the subquery's local FROM/JOIN scope.",
        ]);
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

    /// <summary>Writes the operator structure of a predicate over single-letter columns.</summary>
    private static string Shape(SqlExpression? expression) => expression switch
    {
        SqlLogicalExpression logical => $"Logical({logical.Operator}, [{string.Join(", ", logical.Operands.Select(Shape))}])",
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } not => $"Not({Shape(not.Operand)})",
        SqlBetweenExpression between => $"Between({Shape(between.Operand)})",
        SqlColumnReferenceExpression column => column.ColumnName,
        _ => expression?.GetType().Name ?? "null",
    };

    /// <summary>Recomputes the depth of a predicate over single-letter columns from its nodes.</summary>
    private static int TreeDepth(SqlExpression expression) => expression switch
    {
        SqlLogicalExpression logical => 1 + logical.Operands.Max(TreeDepth),
        SqlUnaryExpression unary => 1 + TreeDepth(unary.Operand),
        SqlColumnReferenceExpression => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression.GetType().Name, "Not a node these predicates use."),
    };

    private static (T? Result, Exception? Failure) RunOnThread<T>(int stackKilobytes, Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, maxStackSize: stackKilobytes * 1024);
        thread.Start();
        thread.Join();
        return (result, failure);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

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

    private static SqlQueryStatement Parse(string sql) => Parse(sql, Limit);

    // Every level of parentheses, calls, CASE or CAST runs the whole precedence ladder, about 6 KB
    // of stack in a debug build (about 1 KB in a release build), so the deepest text the default
    // limit accepts needs more than the 1.5 MB a default thread has in the debug build these tests
    // run in. The statements parse on a thread with ample stack; the stack tests size their own.
    private static SqlQueryStatement Parse(string sql, int limit)
        => OnLargeStack(() => (SqlQueryStatement)new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = limit }).Parse(sql));

    private static T OnLargeStack<T>(Func<T> work)
    {
        var (result, failure) = RunOnThread(LargeStackKilobytes, work);
        failure.ShouldBeNull();
        return result!;
    }

    private const int LargeStackKilobytes = 8 * 1024;

    private static List<Diagnostic> Errors(QueryStatement statement)
        => statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
}
