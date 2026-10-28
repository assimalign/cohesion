using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Renders parsed SQL expressions and queries as canonical SQL text: the one form in which a
/// definition the engine persists (a <c>CHECK</c> predicate, a column <c>DEFAULT</c>, and later
/// expression defaults and views) is stored.
/// </summary>
/// <remarks>
/// <para>
/// The text is a function of the tree alone, never of how the source was written: keywords are
/// upper case, operators and list separators are single-spaced, parentheses preserve the parser's precedence
/// and conservative grouping for persisted predicates, string literals double their embedded quotes, and an
/// identifier is delimited with <c>"</c> only when it is not a plain word of the dialect (a
/// keyword, a builtin function name, a word the parser gives a position-specific meaning, or a
/// name the lexer would not read as one identifier). Names, literal values and parameter names
/// keep their spelling.
/// </para>
/// <para>
/// Parsing the rendered text yields a tree equal to the rendered one, apart from source
/// positions and the whitespace inside a <c>CAST</c> target type, which renders normalized. The
/// guarantee covers trees the parser produced without error diagnostics; a recovery tree that
/// carries an error renders, but need not parse back to itself.
/// </para>
/// </remarks>
public static class SqlExpressionRenderer
{
    /// <summary>Renders an ALTER COLUMN TYPE statement as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The target type, optional collation and original-row conversion.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The action is not ALTER COLUMN TYPE.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlAlterTableExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Action is not SqlAlterColumnTypeAction change)
        {
            throw new NotSupportedException(ErrorMessages.AlterTypeRenderingRequired);
        }
        return "ALTER TABLE " + (query.Table.SchemaName is { } schema ? Identifier(schema) + "." : "") +
            Identifier(query.Table.TableName) + " ALTER COLUMN " + Identifier(change.Column.ColumnName) +
            " TYPE " + AlterTypeTarget(change.Column) + (change.Column.CollationName is { } collation ? " COLLATE " + Identifier(collation) : "") +
            (change.UsingExpression is { } conversion ? " USING " + Render(conversion) : "");
    }

    private static string AlterTypeTarget(SqlColumnDefinition column)
    {
        if (column.ResolvedType is not null) { return CastTarget(column.DataType); }
        return Identifier(column.TypeName);
    }

    // Precedence ladder of SqlQueryParser.Expressions.cs, loosest first. An operand whose own
    // level is below what its position parses is parenthesized.
    private const int orLevel = 1;
    private const int andLevel = 2;
    private const int notLevel = 3;
    private const int isLevel = 4;
    private const int comparisonLevel = 5;
    private const int predicateLevel = 6;
    private const int concatLevel = 7;
    private const int additiveLevel = 8;
    private const int multiplicativeLevel = 9;
    private const int unaryLevel = 11;
    private const int collateLevel = 10;
    private const int primaryLevel = 12;

    // Words that are not profile keywords or functions but that a parser rule reads by spelling
    // in some position: the LIKE escape clause, IS UNKNOWN, and NULLS FIRST/LAST.
    private static readonly string[] _positionalWords = ["ESCAPE", "UNKNOWN", "BOTH", "LEADING", "TRAILING", "NULLS"];

    /// <summary>Renders TRUNCATE as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The table list and explicit effective identity and dependency policies.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlTruncateExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return "TRUNCATE TABLE " + string.Join(", ", query.Tables.Select(target =>
            (target.Only ? "ONLY " : "") + (target.Table.SchemaName is { } schema ? Identifier(schema) + "." : "") +
            Identifier(target.Table.TableName))) + (query.RestartIdentity ? " RESTART IDENTITY" : " CONTINUE IDENTITY") +
            (query.Cascade ? " CASCADE" : " RESTRICT");
    }

    /// <summary>Renders CREATE TABLE AS as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The destination, optional output names, query and explicit data policy.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The query cannot be represented by the dialect.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlCreateTableAsExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return "CREATE TABLE " + (query.IfNotExists ? "IF NOT EXISTS " : "") +
            (query.Table.SchemaName is { } schema ? Identifier(schema) + "." : "") + Identifier(query.Table.TableName) +
            (query.Columns.Count == 0 ? "" : " (" + string.Join(", ", query.Columns.Select(Identifier)) + ")") +
            " AS " + Render(query.Query) + (query.WithData ? " WITH DATA" : " WITH NO DATA");
    }

    // Builtin window functions. An accepted quoted uppercase or mixed-case spelling is a
    // distinct name: keep it delimited so rendering cannot turn it into an unquoted built-in.
    private static readonly string[] _windowFunctions =
        ["ROW_NUMBER", "RANK", "DENSE_RANK", "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE"];

    /// <summary>
    /// Renders a value expression or contextual DEFAULT item as canonical SQL.
    /// </summary>
    /// <param name="expression">An expression produced by <see cref="SqlQueryParser"/>.</param>
    /// <returns>The canonical SQL text of <paramref name="expression"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The tree contains a node the dialect cannot spell: a node type the parser does not
    /// produce. Or the tree
    /// nests deeper than <see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/>, the
    /// highest limit a parser can be configured with, so no parser would read its text.
    /// </exception>
    /// <exception cref="InsufficientExecutionStackException">
    /// The calling thread has too little stack left to walk the tree.
    /// </exception>
    public static string Render(SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ThrowIfTooDeep(expression.Depth);
        return Expression(expression);
    }

    /// <summary>
    /// Renders a <c>SELECT</c> query as canonical SQL, without a terminating <c>;</c>.
    /// </summary>
    /// <param name="query">A query produced by <see cref="SqlQueryParser"/>.</param>
    /// <returns>The canonical SQL text of <paramref name="query"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The query contains a node the dialect cannot spell, or nests deeper than
    /// <see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/>.
    /// </exception>
    /// <exception cref="InsufficientExecutionStackException">
    /// The calling thread has too little stack left to walk the query.
    /// </exception>
    public static string Render(SqlSelectExpression query) => Render((SqlQueryBodyExpression)query);

    /// <summary>Renders a SELECT or set operation as canonical SQL without a terminator.</summary>
    /// <param name="query">A parsed query body.</param>
    /// <returns>The canonical query text.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The tree cannot be represented by the dialect.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlQueryBodyExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooDeep(query.ExpressionDepth);
        return Select(query);
    }

    /// <summary>Renders an INSERT, including WITH and query sources, as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The canonical statement text.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The tree cannot be represented by the dialect.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlInsertExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooDeep(query.NestingDepth);
        var builder = new StringBuilder();
        AppendCommonTables(builder, query.CommonTables);
        builder.Append("INSERT INTO ").Append(Table(query.Table));
        if (query.Columns is { } columns)
        { builder.Append(" (").Append(string.Join(", ", columns.Select(Identifier))).Append(')'); }
        if (query.IsDefaultValues) { builder.Append(" DEFAULT VALUES"); }
        else if (query.SelectSource is { } source) { builder.Append(' ').Append(Select(source)); }
        else
        {
            builder.Append(" VALUES ").Append(string.Join(", ", query.Values!.Select(row =>
                "(" + string.Join(", ", row.Select(Expression)) + ")")));
        }
        if (query.OnConflict is { } conflict)
        {
            builder.Append(" ON CONFLICT");
            if (conflict.Columns.Count > 0)
            { builder.Append(" (").Append(string.Join(", ", conflict.Columns.Select(Identifier))).Append(')'); }
            if (conflict.ConstraintName is { } constraint) { builder.Append(" ON CONSTRAINT ").Append(Identifier(constraint)); }
            if (conflict.Action == SqlOnConflictAction.DoNothing) { builder.Append(" DO NOTHING"); }
            else
            {
                builder.Append(" DO UPDATE SET ").Append(string.Join(", ", conflict.Assignments.Select(assignment =>
                    Identifier(assignment.ColumnName) + " = " + Expression(assignment.Value))));
                if (conflict.Where is { } where) { builder.Append(" WHERE ").Append(Expression(where)); }
            }
        }
        AppendDmlTail(builder, null, query.Returning);
        return builder.ToString();
    }

    /// <summary>Renders an UPDATE, including FROM sources, as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The canonical statement text.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The tree cannot be represented by the dialect.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlUpdateExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooDeep(query.NestingDepth);
        var builder = new StringBuilder();
        AppendCommonTables(builder, query.CommonTables);
        builder.Append("UPDATE ").Append(Table(query.Table)).Append(" SET ");
        builder.Append(string.Join(", ", query.Assignments.Select(assignment => Identifier(assignment.ColumnName) + " = " + Expression(assignment.Value))));
        AppendSources(builder, query.From, " FROM ");
        AppendDmlTail(builder, query.Where, query.Returning);
        return builder.ToString();
    }

    /// <summary>Renders MERGE and its ordered actions as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The canonical statement text.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The tree exceeds the supported nesting limit.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlMergeExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooDeep(query.NestingDepth);
        var builder = new StringBuilder();
        AppendCommonTables(builder, query.CommonTables);
        builder.Append("MERGE INTO ");
        builder.Append(Table(query.Table));
        AppendSources(builder, [query.Source], " USING ");
        builder.Append(" ON ").Append(Expression(query.Condition));
        foreach (var action in query.Actions)
        {
            builder.Append(action.MatchKind switch
            {
                SqlMergeMatchKind.Matched => " WHEN MATCHED",
                SqlMergeMatchKind.NotMatchedBySource => " WHEN NOT MATCHED BY SOURCE",
                _ => action.IsByTargetExplicit ? " WHEN NOT MATCHED BY TARGET" : " WHEN NOT MATCHED",
            });
            if (action.Condition is { } condition) { builder.Append(" AND ").Append(Expression(condition)); }
            builder.Append(" THEN ");
            switch (action.Action)
            {
                case SqlMergeAction.Update:
                    builder.Append("UPDATE SET ").Append(string.Join(", ", action.Assignments.Select(assignment => Identifier(assignment.ColumnName) + " = " + Expression(assignment.Value))));
                    break;
                case SqlMergeAction.Delete: builder.Append("DELETE"); break;
                case SqlMergeAction.DoNothing: builder.Append("DO NOTHING"); break;
                case SqlMergeAction.Insert:
                    builder.Append("INSERT");
                    if (action.Columns is { } columns) { builder.Append(" (").Append(string.Join(", ", columns.Select(Identifier))).Append(')'); }
                    if (action.IsDefaultValues) { builder.Append(" DEFAULT VALUES"); }
                    else { builder.Append(" VALUES (").Append(string.Join(", ", action.Values!.Select(Expression))).Append(')'); }
                    break;
            }
        }
        AppendDmlTail(builder, null, query.Returning);
        return builder.ToString();
    }

    /// <summary>Renders a DELETE, including USING sources, as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed statement.</param>
    /// <returns>The canonical statement text.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="NotSupportedException">The tree cannot be represented by the dialect.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has insufficient stack.</exception>
    public static string Render(SqlDeleteExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfTooDeep(query.NestingDepth);
        var builder = new StringBuilder();
        AppendCommonTables(builder, query.CommonTables);
        builder.Append("DELETE FROM ").Append(Table(query.Table));
        AppendSources(builder, query.Using, " USING ");
        AppendDmlTail(builder, query.Where, query.Returning);
        return builder.ToString();
    }

    private static void AppendDmlTail(StringBuilder builder, SqlExpression? where, IReadOnlyList<SqlSelectColumn>? returning)
    {
        if (where is not null) { builder.Append(" WHERE ").Append(Expression(where)); }
        if (returning is null) { return; }
        builder.Append(" RETURNING ");
        builder.Append(string.Join(", ", returning.Select(column => Expression(column.Expression) +
            (column.Alias is null ? string.Empty : " AS " + Identifier(column.Alias)))));
    }

    /// <summary>
    /// Refuses a tree deeper than any parser accepts (#1151): the highest limit a parser can be
    /// configured with. Its text would not parse back, so it has no canonical form; and refusing it
    /// before the walk keeps the walk's recursion within that ceiling. The renderer adds at most
    /// one pair of parentheses per node, so the text of a tree nests its parentheses no deeper than
    /// the tree: it parses under every limit at least as deep as the tree, including the one of the
    /// parser that built it.
    /// </summary>
    private static void ThrowIfTooDeep(int depth)
    {
        if (depth > SqlQueryParserOptions.MaximumExpressionNestingLimit)
        {
            throw new NotSupportedException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererThrowIfTooDeepNotSupported, depth, SqlQueryParserOptions.MaximumExpressionNestingLimit));
        }
    }

    private static string Expression(SqlExpression expression)
    {
        // Every recursive step of the walk passes here, including the clauses of a subquery.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return Node(expression);
    }

    private static string Node(SqlExpression expression) => expression switch
    {
        SqlLogicalExpression logical => Logical(logical),
        SqlBinaryExpression binary => Binary(binary),
        SqlUnaryExpression unary => Unary(unary),
        SqlLiteralExpression literal => Literal(literal),
        SqlDefaultExpression => "DEFAULT",
        SqlColumnReferenceExpression column => Column(column),
        SqlParameterExpression parameter => parameter.ParameterName,
        SqlStarExpression => "*",
        SqlDistinctExpression distinct => Operand(distinct.Left, concatLevel) +
            (distinct.IsNegated ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ") + Operand(distinct.Right, comparisonLevel),
        SqlBooleanTestExpression test => Operand(test.Operand, concatLevel) + (test.IsNegated ? " IS NOT " : " IS ") + test.TruthValue.ToString().ToUpperInvariant(),
        SqlIsNullExpression isNull => Operand(isNull.Operand, concatLevel) + (isNull.IsNegated ? " IS NOT NULL" : " IS NULL"),
        SqlBetweenExpression between => Operand(between.Operand, concatLevel) + (between.IsNegated ? " NOT BETWEEN " : " BETWEEN ") +
            Operand(between.Low, concatLevel) + " AND " + Operand(between.High, concatLevel),
        SqlInExpression inExpression => In(inExpression),
        SqlQuantifiedComparisonExpression quantified => Operand(quantified.Operand, predicateLevel) + " " +
            OperatorText(quantified.Operator) + " " + quantified.Quantifier.ToString().ToUpperInvariant() +
            " (" + Select(quantified.Subquery) + ")",
        SqlLikeExpression like => Operand(like.Operand, concatLevel) + (like.IsNegated ? " NOT LIKE " : " LIKE ") +
            Operand(like.Pattern, collateLevel) + (like.Escape is null ? "" : " ESCAPE " + Operand(like.Escape, collateLevel)),
        SqlCollateExpression collate => Operand(collate.Operand, collateLevel) + " COLLATE " + collate.CollationName,
        SqlFunctionCallExpression function when SqlUnsupportedVocabulary.IsNiladicFunction(function.FunctionName) && function.Arguments.Count == 0 && function.Quantifier == SqlSetQuantifier.Default && function.OrderBy.Count == 0 => function.FunctionName,
        SqlFunctionCallExpression function when function.FunctionName.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase) && function.Arguments.Count == 2 && function.Arguments[0] is SqlLiteralExpression field && function.Quantifier == SqlSetQuantifier.Default && function.OrderBy.Count == 0
            => "EXTRACT(" + field.Value + " FROM " + Expression(function.Arguments[1]) + ")",
        SqlFunctionCallExpression function when function.FunctionName.Equals("POSITION", StringComparison.OrdinalIgnoreCase) && function.Arguments.Count == 2 && function.Quantifier == SqlSetQuantifier.Default && function.OrderBy.Count == 0
            => "POSITION(" + Operand(function.Arguments[0], concatLevel) + " IN " + Expression(function.Arguments[1]) + ")",
        SqlFunctionCallExpression function => AggregateCall(function),
        SqlCaseExpression caseExpression => Case(caseExpression),
        SqlCastExpression cast => "CAST(" + Expression(cast.Operand) + " AS " + CastTarget(cast.TargetType) + ")",
        SqlExistsExpression exists => (exists.IsNegated ? "NOT EXISTS (" : "EXISTS (") + Select(exists.Subquery) + ")",
        SqlSubqueryExpression subquery => "(" + Select(subquery.Select) + ")",
        _ => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlExpressionRendererNodeNotSupported, expression.GetType().Name)),
    };

    private static string Ordering(SqlOrderByColumn key) => Expression(key.Expression) + (key.IsDescending ? " DESC" : "") +
        (key.NullPlacement switch { SqlNullPlacement.First => " NULLS FIRST", SqlNullPlacement.Last => " NULLS LAST", _ => "" });

    private static string AggregateCall(SqlFunctionCallExpression call)
    {
        string quantifier = call.Quantifier switch { SqlSetQuantifier.Distinct => "DISTINCT ", SqlSetQuantifier.All => "ALL ", _ => "" };
        string ordering = call.OrderBy.Count == 0 ? "" : "ORDER BY " + string.Join(", ", call.OrderBy.Select(key =>
            Ordering(key)));
        bool listaggName = call.FunctionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase);
        bool listagg = call.HasWithinGroupOrdering;
        // A delimited mixed/upper-case LISTAGG is an ordinary call in the parser.
        // With no WITHIN GROUP keys it must stay delimited on a round trip.
        bool specialForm = SqlUnsupportedVocabulary.IsNiladicFunction(call.FunctionName) ||
            call.FunctionName.ToUpperInvariant() is "EXTRACT" or "POSITION" or "SUBSTRING" or "TRIM";
        string name = (listaggName && !listagg) || (specialForm &&
            (call.Quantifier != SqlSetQuantifier.Default || call.OrderBy.Count > 0))
            ? "\"" + call.FunctionName + "\"" : FunctionName(call.FunctionName);
        return name + "(" + quantifier + Arguments(call.Arguments) +
            (!listagg && ordering.Length > 0 ? " " + ordering : "") + ")" +
            (listagg ? " WITHIN GROUP (" + ordering + ")" : "");
    }

    /// <summary>The precedence level the parser assigns the node.</summary>
    private static int Level(SqlExpression expression) => expression switch
    {
        SqlLogicalExpression { Operator: SqlLogicalOperator.Or } => orLevel,
        SqlLogicalExpression => andLevel,
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => notLevel,
        // NOT EXISTS is read by the NOT rung itself, so the negated form sits at that level.
        SqlExistsExpression { IsNegated: true } => notLevel,
        SqlBinaryExpression
        {
            Operator: SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan or
                SqlBinaryOperator.GreaterThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterOrEqual,
        } => comparisonLevel,
        SqlIsNullExpression or SqlDistinctExpression or SqlBooleanTestExpression => isLevel,
        SqlQuantifiedComparisonExpression => comparisonLevel,
        SqlBetweenExpression or SqlInExpression or SqlLikeExpression => predicateLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract } => additiveLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.Concat } => concatLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo } => multiplicativeLevel,
        SqlBinaryExpression binary => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionRendererLevelNotSupported, binary.Operator)),
        SqlUnaryExpression => unaryLevel,
        SqlCollateExpression => collateLevel,
        _ => primaryLevel,
    };

    /// <summary>Renders an operand, parenthesized when its position parses a tighter level.</summary>
    private static string Operand(SqlExpression operand, int minimumLevel)
        => Level(operand) < minimumLevel ? "(" + Expression(operand) + ")" : Expression(operand);

    private static string Binary(SqlBinaryExpression binary)
    {
        int level = Level(binary);

        // Arithmetic operators associate to the left, so a right operand at the operator's own
        // level needs parentheses. Comparisons do not chain at all.
        int right = level == comparisonLevel ? concatLevel : level + 1;
        int left = level == comparisonLevel ? concatLevel : level;
        return Operand(binary.Left, left) + " " + OperatorText(binary.Operator) + " " + Operand(binary.Right, right);
    }

    /// <summary>
    /// Renders an <c>AND</c> or <c>OR</c> chain term by term, iterating however many terms it has
    /// (#1151). Every term is parenthesized when it parses below the next rung, so a nested chain
    /// of the same operator keeps its parentheses and reads back as the nested node it is. The
    /// parser merges such a chain only in first position, where it never leaves one, so the text
    /// is the one the binary form rendered: <c>a AND b AND c</c>, <c>a AND (b AND c)</c>.
    /// </summary>
    private static string Logical(SqlLogicalExpression logical)
    {
        int level = Level(logical);
        string separator = logical.Operator == SqlLogicalOperator.And ? " AND " : " OR ";
        var builder = new StringBuilder();
        for (int index = 0; index < logical.Operands.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(separator);
            }

            builder.Append(Operand(logical.Operands[index], level + 1));
        }

        return builder.ToString();
    }

    private static string OperatorText(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        SqlBinaryOperator.Modulo => "%",
        SqlBinaryOperator.Concat => "||",
        SqlBinaryOperator.Equal => "=",
        SqlBinaryOperator.NotEqual => "<>",
        SqlBinaryOperator.LessThan => "<",
        SqlBinaryOperator.GreaterThan => ">",
        SqlBinaryOperator.LessOrEqual => "<=",
        SqlBinaryOperator.GreaterOrEqual => ">=",
        // AND and OR are SqlLogicalExpression chains; the parser builds no binary node for them.
        _ => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionRendererLevelNotSupported, op)),
    };

    private static string Unary(SqlUnaryExpression unary)
    {
        if (unary.Operator == SqlUnaryOperator.Not)
        {
            // NOT EXISTS (...) is one construct to the parser: rendered bare, NOT applied to a
            // plain EXISTS, or to an operand that starts with one, would fold into it.
            string operand = Operand(unary.Operand, notLevel);
            return "NOT " + (operand.StartsWith("EXISTS (", StringComparison.Ordinal) ? "(" + operand + ")" : operand);
        }

        string sign = unary.Operator switch
        {
            SqlUnaryOperator.Negate => "-",
            SqlUnaryOperator.Plus => "+",
            SqlUnaryOperator.BitwiseNot => "~",
            _ => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionRendererUnaryNotSupported, unary.Operator)),
        };

        // A sign applied to a sign is parenthesized: two minus signs in a row would start a
        // line comment, and a run of ~ reads as one operator.
        string text = unary.Operand is SqlUnaryExpression { Operator: not SqlUnaryOperator.Not }
            ? "(" + Expression(unary.Operand) + ")"
            : Operand(unary.Operand, unaryLevel);

        // A + directly before a numeric literal is part of the literal, so +(1) keeps its
        // parentheses to stay a unary plus.
        if (unary.Operator == SqlUnaryOperator.Plus && text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] == '.'))
        {
            text = "(" + text + ")";
        }

        return sign + text;
    }

    private static string Literal(SqlLiteralExpression literal) => literal.LiteralType switch
    {
        SqlLiteralType.String => "'" + literal.Value.Replace("'", "''", StringComparison.Ordinal) + "'",
        SqlLiteralType.Date => "DATE '" + literal.Value + "'",
        SqlLiteralType.Time => "TIME '" + literal.Value + "'",
        SqlLiteralType.Timestamp => "TIMESTAMP '" + literal.Value + "'",
        SqlLiteralType.TimestampWithTimeZone => "TIMESTAMPTZ '" + literal.Value + "'",
        SqlLiteralType.Interval => "INTERVAL '" + literal.Value + "'",
        SqlLiteralType.Binary => "X'" + literal.Value + "'",
        SqlLiteralType.Null => "NULL",
        SqlLiteralType.Boolean => literal.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "TRUE" : "FALSE",
        SqlLiteralType.Integer or SqlLiteralType.Float => literal.Value,
        _ => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionRendererLiteralNotSupported, literal.LiteralType)),
    };

    private static string Column(SqlColumnReferenceExpression column)
    {
        var builder = new StringBuilder();
        if (column.SchemaName is not null)
        {
            builder.Append(Identifier(column.SchemaName)).Append('.');
        }
        if (column.TableAlias is not null)
        {
            // OLD/NEW are contextual RETURNING pseudo-relations. The AST keeps ordinary
            // identifier values, so delimit these qualifiers to preserve that meaning.
            builder.Append(column.TableAlias.Equals("old", StringComparison.OrdinalIgnoreCase) ||
                column.TableAlias.Equals("new", StringComparison.OrdinalIgnoreCase)
                ? "\"" + column.TableAlias + "\"" : Identifier(column.TableAlias)).Append('.');
        }
        return builder.Append(Identifier(column.ColumnName)).ToString();
    }

    private static string In(SqlInExpression expression)
    {
        string list = expression.Subquery is not null
            ? Select(expression.Subquery)
            : List(expression.Values ?? Array.Empty<SqlExpression>());
        return Operand(expression.Operand, concatLevel) + (expression.IsNegated ? " NOT IN (" : " IN (") + list + ")";
    }

    private static string Case(SqlCaseExpression expression)
    {
        var builder = new StringBuilder("CASE");
        if (expression.Input is not null)
        {
            builder.Append(' ').Append(Expression(expression.Input));
        }
        foreach (var clause in expression.WhenClauses)
        {
            builder.Append(" WHEN ").Append(Expression(clause.Condition))
                   .Append(" THEN ").Append(Expression(clause.Result));
        }
        if (expression.ElseResult is not null)
        {
            builder.Append(" ELSE ").Append(Expression(expression.ElseResult));
        }
        return builder.Append(" END").ToString();
    }

    private static string List(IReadOnlyList<SqlExpression> expressions)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < expressions.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }
            builder.Append(Expression(expressions[index]));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Normalizes the CAST target as written, such as <c>decimal( 10 , 2 )</c>, to
    /// <c>DECIMAL(10, 2)</c>: the type name upper case and the arguments comma-separated, with
    /// comments removed and one space between type-name words.
    /// </summary>
    private static string CastTarget(string targetType)
    {
        var builder = new StringBuilder();
        var lexer = new TokenLexer(targetType, SqlLanguageProfile.Instance.ToLexerOptions());
        bool previousWord = false;
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type == TokenType.Comment)
            {
                continue;
            }

            string token = lexer.Current.Value.ToString();
            bool word = lexer.Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function or TokenType.QuotedIdentifier;
            if (word && previousWord) { builder.Append(' '); }
            builder.Append(lexer.Current.Type == TokenType.Comma ? ", " : word ? token.ToUpperInvariant() : token);
            previousWord = word;
        }
        return builder.ToString();
    }

    private static string Select(SqlQueryBodyExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (query.CommonTables.Count > 0)
        {
            foreach (var definition in query.CommonTables) { ThrowIfTooDeep(definition.Body.ExpressionDepth); }
        }
        var builder = new StringBuilder();
        AppendCommonTables(builder, query.CommonTables);
        if (query is SqlSetOperationExpression set)
        {
            builder.Append(SetOperand(set.Left, set.Operator, false)).Append(set.Operator switch
            {
                SqlSetOperator.Union => " UNION",
                SqlSetOperator.Intersect => " INTERSECT",
                _ => " EXCEPT",
            }).Append(set.IsAll ? " ALL " : " ").Append(SetOperand(set.Right, set.Operator, true));
            AppendQueryTail(builder, query);
            return builder.ToString();
        }
        var select = (SqlSelectExpression)query;
        builder.Append("SELECT ");
        if (select.IsDistinct)
        {
            builder.Append("DISTINCT ");
        }
        if (select.LimitSyntaxKind == SqlLimitSyntaxKind.Top)
        {
            builder.Append("TOP (").Append(Expression(select.Limit!)).Append(") ");
        }

        for (int index = 0; index < select.Columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }
            var column = select.Columns[index];
            builder.Append(Expression(column.Expression));
            if (column.Alias is not null)
            {
                builder.Append(" AS ").Append(Identifier(column.Alias));
            }
        }

        AppendSources(builder, select.From, " FROM ");
        if (select.Where is not null)
        {
            builder.Append(" WHERE ").Append(Expression(select.Where));
        }
        if (select.GroupBy.Count > 0)
        {
            builder.Append(" GROUP BY ").Append(List(select.GroupBy));
        }
        if (select.Having is not null)
        {
            builder.Append(" HAVING ").Append(Expression(select.Having));
        }
        AppendQueryTail(builder, select);
        return builder.ToString();
    }

    private static void AppendCommonTables(StringBuilder builder, IReadOnlyList<SqlCommonTableExpression> definitions)
    {
        if (definitions.Count > 0)
        {
            builder.Append(definitions[0].IsRecursiveScope ? "WITH RECURSIVE " : "WITH ");
            for (int i = 0; i < definitions.Count; i++)
            {
                if (i > 0) { builder.Append(", "); }
                var definition = definitions[i];
                builder.Append(Identifier(definition.Name));
                if (definition.Columns.Count > 0)
                {
                    builder.Append(" (").Append(string.Join(", ", definition.Columns.Select(Identifier))).Append(')');
                }
                builder.Append(" AS (").Append(Select(definition.Body)).Append(')');
            }
            builder.Append(' ');
        }
    }

    private static void AppendSources(StringBuilder builder, IReadOnlyList<SqlFromItem> sources, string clause)
    {
        for (int itemIndex = 0; itemIndex < sources.Count; itemIndex++)
        {
            var item = sources[itemIndex];
            builder.Append(itemIndex == 0 ? clause : ", ").Append(Table(item.Table));
            foreach (var join in item.Joins)
            {
                builder.Append(join.JoinType switch
                {
                    SqlJoinType.Inner => " INNER JOIN ",
                    SqlJoinType.LeftOuter => " LEFT OUTER JOIN ",
                    SqlJoinType.RightOuter => " RIGHT OUTER JOIN ",
                    SqlJoinType.FullOuter => " FULL OUTER JOIN ",
                    SqlJoinType.Cross => " CROSS JOIN ",
                    _ => throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionRendererSelectNotSupported, join.JoinType)),
                }).Append(Table(join.Table));
                if (join.Condition is not null)
                {
                    builder.Append(" ON ").Append(Expression(join.Condition));
                }
                if (join.UsingColumns.Count > 0)
                {
                    builder.Append(" USING (").Append(string.Join(", ", join.UsingColumns.Select(Identifier))).Append(')');
                }
            }
        }
    }

    private static string SetOperand(SqlQueryBodyExpression operand, SqlSetOperator parent, bool right)
    {
        int precedence = parent == SqlSetOperator.Intersect ? 2 : 1;
        int childPrecedence = operand is SqlSetOperationExpression child ? child.Operator == SqlSetOperator.Intersect ? 2 : 1 : 3;
        bool parentheses = operand.CommonTables.Count > 0 || operand.OrderBy.Count > 0 || operand.Limit is not null || operand.Offset is not null ||
            childPrecedence < precedence || right && childPrecedence == precedence;
        string text = Select(operand);
        return parentheses ? "(" + text + ")" : text;
    }

    private static void AppendQueryTail(StringBuilder builder, SqlQueryBodyExpression query)
    {
        for (int index = 0; index < query.OrderBy.Count; index++)
        {
            builder.Append(index == 0 ? " ORDER BY " : ", ").Append(Ordering(query.OrderBy[index]));
        }
        if (query.LimitSyntaxKind == SqlLimitSyntaxKind.Limit)
        {
            builder.Append(" LIMIT ").Append(Expression(query.Limit!));
        }
        if (query.Offset is not null)
        {
            builder.Append(" OFFSET ").Append(Expression(query.Offset));
        }
        if (query.LimitSyntaxKind == SqlLimitSyntaxKind.Fetch)
        {
            var count = query.Limit!;
            bool signedNumeric = count is SqlUnaryExpression
            {
                Operator: SqlUnaryOperator.Negate,
                Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float },
            };
            builder.Append(" FETCH FIRST ").Append(signedNumeric ? Expression(count) : Operand(count, primaryLevel))
                .Append(" ROWS ONLY");
        }
    }

    private static string Table(SqlRelationReference relation)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (relation is SqlJoinedTableReference group)
        {
            var builder = new StringBuilder("(");
            AppendSources(builder, [group.Item], string.Empty);
            return builder.Append(')').ToString();
        }
        if (relation is SqlDerivedTableReference derived)
        {
            return "(" + Select(derived.Query) + ") AS " + Identifier(derived.Alias!) +
                (derived.Columns.Count == 0 ? string.Empty : " (" + string.Join(", ", derived.Columns.Select(Identifier)) + ")");
        }
        var table = (SqlTableReference)relation;
        string name = table.SchemaName is null
            ? Identifier(table.TableName)
            : Identifier(table.SchemaName) + "." + Identifier(table.TableName);
        return table.Alias is null ? name : name + " AS " + Identifier(table.Alias);
    }

    /// <summary>
    /// Spells a name bare when the lexer reads it back as one plain identifier, and delimited
    /// otherwise. Delimiting never changes the name: the parser strips the quotes.
    /// </summary>
    private static string Identifier(string name)
    {
        return IsRegularIdentifier(name) && !IsReservedWord(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Spells a function name bare when the bare word parses as a call to it: a builtin
    /// function, or a plain identifier. <c>CAST</c> and every other keyword are delimited,
    /// because the bare keyword starts its own construct, and so are the window function names,
    /// whose bare call the parser rejects.
    /// </summary>
    private static string FunctionName(string name)
    {
        bool bare = IsRegularIdentifier(name) && !name.Equals("CAST", StringComparison.OrdinalIgnoreCase) &&
            !Contains(_windowFunctions, name) &&
            (Contains(SqlLanguageProfile.Instance.Functions, name) || !IsReservedWord(name));
        return bare ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Renders call arguments. A leading <c>*</c> right after the open parenthesis is read as the
    /// <c>COUNT(*)</c> star argument, so a first argument that is not the star itself but whose
    /// text starts with one, such as <c>(*) = 1</c>, is parenthesized.
    /// </summary>
    private static string Arguments(IReadOnlyList<SqlExpression> arguments)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < arguments.Count; index++)
        {
            string text = Expression(arguments[index]);
            if (index > 0)
            {
                builder.Append(", ");
            }
            else if (arguments[index] is not SqlStarExpression && text.StartsWith('*'))
            {
                text = "(" + text + ")";
            }
            builder.Append(text);
        }
        return builder.ToString();
    }

    /// <summary>Mirrors the lexer's identifier scan: a letter or <c>_</c>, then letters, digits and <c>_</c>.</summary>
    private static bool IsRegularIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        for (int index = 1; index < name.Length; index++)
        {
            if (!(char.IsLetterOrDigit(name[index]) || name[index] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedWord(string name)
    {
        if (Contains(SqlLanguageProfile.Instance.Keywords, name) || Contains(SqlLanguageProfile.Instance.Functions, name) ||
            Contains(_positionalWords, name))
        {
            return true;
        }

        foreach (var word in SqlUnsupportedVocabulary.Words)
        {
            if (word.Spelling.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(ReadOnlySpan<string> words, string name)
    {
        foreach (string word in words)
        {
            if (word.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
