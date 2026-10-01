using System;
using System.Collections.Generic;
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
/// upper case, operators and list separators are single-spaced, parentheses appear only where
/// the parser's precedence requires them, string literals double their embedded quotes, and an
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
    // Precedence ladder of SqlQueryParser.Expressions.cs, loosest first. An operand whose own
    // level is below what its position parses is parenthesized.
    private const int orLevel = 1;
    private const int andLevel = 2;
    private const int notLevel = 3;
    private const int comparisonLevel = 4;
    private const int additiveLevel = 5;
    private const int multiplicativeLevel = 6;
    private const int unaryLevel = 7;
    private const int collateLevel = 8;
    private const int primaryLevel = 9;

    // Words that are not profile keywords or functions but that a parser rule reads by spelling
    // in some position: the LIKE escape clause, IS UNKNOWN, and NULLS FIRST/LAST.
    private static readonly string[] _positionalWords = ["ESCAPE", "UNKNOWN", "NULLS"];

    // Builtin window functions. The parser rejects a bare call to one as an unsupported window
    // function, so a call that parsed without error named a user function delimited, and is
    // rendered delimited.
    private static readonly string[] _windowFunctions =
        ["ROW_NUMBER", "RANK", "DENSE_RANK", "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE"];

    /// <summary>
    /// Renders a scalar expression as canonical SQL.
    /// </summary>
    /// <param name="expression">An expression produced by <see cref="SqlQueryParser"/>.</param>
    /// <returns>The canonical SQL text of <paramref name="expression"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The tree contains a node the dialect cannot spell: a node type the parser does not
    /// produce, or a name containing a double quote, which the dialect cannot delimit.
    /// </exception>
    public static string Render(SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Expression(expression);
    }

    /// <summary>
    /// Renders a <c>SELECT</c> query as canonical SQL, without a terminating <c>;</c>.
    /// </summary>
    /// <param name="query">A query produced by <see cref="SqlQueryParser"/>.</param>
    /// <returns>The canonical SQL text of <paramref name="query"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="NotSupportedException">The query contains a node the dialect cannot spell.</exception>
    public static string Render(SqlSelectExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Select(query);
    }

    private static string Expression(SqlExpression expression) => expression switch
    {
        SqlBinaryExpression binary => Binary(binary),
        SqlUnaryExpression unary => Unary(unary),
        SqlLiteralExpression literal => Literal(literal),
        SqlColumnReferenceExpression column => Column(column),
        SqlParameterExpression parameter => parameter.ParameterName,
        SqlStarExpression => "*",
        SqlIsNullExpression isNull => Operand(isNull.Operand, additiveLevel) + (isNull.IsNegated ? " IS NOT NULL" : " IS NULL"),
        SqlBetweenExpression between => Operand(between.Operand, additiveLevel) + (between.IsNegated ? " NOT BETWEEN " : " BETWEEN ") +
            Operand(between.Low, additiveLevel) + " AND " + Operand(between.High, additiveLevel),
        SqlInExpression inExpression => In(inExpression),
        SqlLikeExpression like => Operand(like.Operand, additiveLevel) + (like.IsNegated ? " NOT LIKE " : " LIKE ") +
            Operand(like.Pattern, collateLevel),
        SqlCollateExpression collate => Operand(collate.Operand, collateLevel) + " COLLATE " + collate.CollationName,
        SqlFunctionCallExpression function => FunctionName(function.FunctionName) + "(" + Arguments(function.Arguments) + ")",
        SqlCaseExpression caseExpression => Case(caseExpression),
        SqlCastExpression cast => "CAST(" + Expression(cast.Operand) + " AS " + CastTarget(cast.TargetType) + ")",
        SqlExistsExpression exists => (exists.IsNegated ? "NOT EXISTS (" : "EXISTS (") + Select(exists.Subquery) + ")",
        SqlSubqueryExpression subquery => "(" + Select(subquery.Select) + ")",
        _ => throw new NotSupportedException($"Expression node '{expression.GetType().Name}' has no SQL spelling."),
    };

    /// <summary>The precedence level the parser assigns the node.</summary>
    private static int Level(SqlExpression expression) => expression switch
    {
        SqlBinaryExpression { Operator: SqlBinaryOperator.Or } => orLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.And } => andLevel,
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => notLevel,
        // NOT EXISTS is read by the NOT rung itself, so the negated form sits at that level.
        SqlExistsExpression { IsNegated: true } => notLevel,
        SqlBinaryExpression
        {
            Operator: SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan or
                SqlBinaryOperator.GreaterThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterOrEqual,
        } => comparisonLevel,
        SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlLikeExpression => comparisonLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Concat } => additiveLevel,
        SqlBinaryExpression { Operator: SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo } => multiplicativeLevel,
        SqlBinaryExpression binary => throw new NotSupportedException($"Binary operator '{binary.Operator}' has no SQL spelling."),
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

        // Logical and arithmetic operators associate to the left, so a right operand at the
        // operator's own level needs parentheses. Comparisons do not chain at all.
        int right = level == comparisonLevel ? additiveLevel : level + 1;
        int left = level == comparisonLevel ? additiveLevel : level;
        return Operand(binary.Left, left) + " " + OperatorText(binary.Operator) + " " + Operand(binary.Right, right);
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
        SqlBinaryOperator.And => "AND",
        SqlBinaryOperator.Or => "OR",
        _ => throw new NotSupportedException($"Binary operator '{op}' has no SQL spelling."),
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
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' has no SQL spelling."),
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
        SqlLiteralType.Null => "NULL",
        SqlLiteralType.Boolean => literal.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "TRUE" : "FALSE",
        SqlLiteralType.Integer or SqlLiteralType.Float => literal.Value,
        _ => throw new NotSupportedException($"Literal type '{literal.LiteralType}' has no SQL spelling."),
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
            builder.Append(Identifier(column.TableAlias)).Append('.');
        }
        return builder.Append(Identifier(column.ColumnName)).ToString();
    }

    private static string In(SqlInExpression expression)
    {
        string list = expression.Subquery is not null
            ? Select(expression.Subquery)
            : List(expression.Values ?? Array.Empty<SqlExpression>());
        return Operand(expression.Operand, additiveLevel) + (expression.IsNegated ? " NOT IN (" : " IN (") + list + ")";
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
    /// comments and other whitespace removed.
    /// </summary>
    private static string CastTarget(string targetType)
    {
        var builder = new StringBuilder();
        var lexer = new TokenLexer(targetType, SqlLanguageProfile.Instance.ToLexerOptions());
        bool first = true;
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type == TokenType.Comment)
            {
                continue;
            }

            string token = lexer.Current.Value.ToString();
            builder.Append(lexer.Current.Type == TokenType.Comma ? ", " : first ? token.ToUpperInvariant() : token);
            first = false;
        }
        return builder.ToString();
    }

    private static string Select(SqlSelectExpression select)
    {
        var builder = new StringBuilder("SELECT ");
        if (select.IsDistinct)
        {
            builder.Append("DISTINCT ");
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

        if (select.From is not null)
        {
            builder.Append(" FROM ").Append(Table(select.From));
        }
        foreach (var join in select.Joins)
        {
            builder.Append(join.JoinType switch
            {
                SqlJoinType.Inner => " INNER JOIN ",
                SqlJoinType.LeftOuter => " LEFT OUTER JOIN ",
                SqlJoinType.RightOuter => " RIGHT OUTER JOIN ",
                SqlJoinType.FullOuter => " FULL OUTER JOIN ",
                SqlJoinType.Cross => " CROSS JOIN ",
                _ => throw new NotSupportedException($"Join type '{join.JoinType}' has no SQL spelling."),
            }).Append(Table(join.Table));
            if (join.Condition is not null)
            {
                builder.Append(" ON ").Append(Expression(join.Condition));
            }
        }
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
        for (int index = 0; index < select.OrderBy.Count; index++)
        {
            builder.Append(index == 0 ? " ORDER BY " : ", ").Append(Expression(select.OrderBy[index].Expression));
            if (select.OrderBy[index].IsDescending)
            {
                builder.Append(" DESC");
            }
        }
        if (select.Limit is not null)
        {
            builder.Append(" LIMIT ").Append(Expression(select.Limit));
        }
        if (select.Offset is not null)
        {
            builder.Append(" OFFSET ").Append(Expression(select.Offset));
        }
        return builder.ToString();
    }

    private static string Table(SqlTableReference table)
    {
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
        ThrowIfUndelimitable(name);
        return IsRegularIdentifier(name) && !IsReservedWord(name) ? name : "\"" + name + "\"";
    }

    /// <summary>
    /// Spells a function name bare when the bare word parses as a call to it: a builtin
    /// function, or a plain identifier. <c>CAST</c> and every other keyword are delimited,
    /// because the bare keyword starts its own construct, and so are the window function names,
    /// whose bare call the parser rejects.
    /// </summary>
    private static string FunctionName(string name)
    {
        ThrowIfUndelimitable(name);
        bool bare = IsRegularIdentifier(name) && !name.Equals("CAST", StringComparison.OrdinalIgnoreCase) &&
            !Contains(_windowFunctions, name) &&
            (Contains(SqlLanguageProfile.Instance.Functions, name) || !IsReservedWord(name));
        return bare ? name : "\"" + name + "\"";
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

    private static void ThrowIfUndelimitable(string name)
    {
        if (name.Contains('"', StringComparison.Ordinal))
        {
            throw new NotSupportedException($"The name '{name}' contains a double quote, which the SQL dialect cannot delimit.");
        }
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
