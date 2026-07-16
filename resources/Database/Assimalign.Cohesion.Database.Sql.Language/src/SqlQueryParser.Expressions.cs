using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    // ── Expression parsing (recursive descent with precedence) ─────────
    //
    //   ParseExpression         → ParseOr
    //   ParseOr                 → ParseAnd (OR ParseAnd)*
    //   ParseAnd                → ParseNot (AND ParseNot)*
    //   ParseNot                → NOT? ParseComparison
    //   ParseComparison         → ParseAddition ((=|<>|<|>|<=|>=) ParseAddition
    //                            | IS [NOT] NULL
    //                            | [NOT] BETWEEN ... AND ...
    //                            | [NOT] IN (...)
    //                            | [NOT] LIKE ...)?
    //   ParseAddition           → ParseMultiplication ((+|-||) ParseMultiplication)*
    //   ParseMultiplication     → ParseUnary ((*|/|%) ParseUnary)*
    //   ParseUnary              → (-|~)? ParsePrimary
    //   ParsePrimary            → literal | column_ref | param | function(...)
    //                            | (expr) | (SELECT ...) | CASE | CAST | EXISTS | *

    private SqlExpression ParseExpression(ref TokenLexer lexer)
    {
        return ParseOr(ref lexer);
    }

    private SqlExpression ParseOr(ref TokenLexer lexer)
    {
        var left = ParseAnd(ref lexer);

        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OR"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseAnd(ref lexer);
            left = new SqlBinaryExpression(left, SqlBinaryOperator.Or, right,
                Location.Create(1, 1, pos, pos));
        }

        return left;
    }

    private SqlExpression ParseAnd(ref TokenLexer lexer)
    {
        var left = ParseNot(ref lexer);

        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AND"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseNot(ref lexer);
            left = new SqlBinaryExpression(left, SqlBinaryOperator.And, right,
                Location.Create(1, 1, pos, pos));
        }

        return left;
    }

    private SqlExpression ParseNot(ref TokenLexer lexer)
    {
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NOT"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);

            // Check for NOT EXISTS
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "EXISTS"))
            {
                return ParseExists(ref lexer, isNegated: true, pos);
            }

            var operand = ParseNot(ref lexer);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Not,
                Location.Create(1, 1, pos, pos));
        }

        return ParseComparison(ref lexer);
    }

    private SqlExpression ParseComparison(ref TokenLexer lexer)
    {
        var left = ParseAddition(ref lexer);

        if (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon)
        {
            return left;
        }

        // IS [NOT] NULL
        if (IsKeyword(ref lexer, "IS"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            bool negated = false;
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NOT"))
            {
                negated = true;
                Advance(ref lexer);
            }
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NULL"))
            {
                Advance(ref lexer);
            }
            return new SqlIsNullExpression(left, negated, Location.Create(1, 1, pos, pos));
        }

        // [NOT] BETWEEN ... AND ...
        bool notBefore = false;
        if (IsKeyword(ref lexer, "NOT"))
        {
            // Peek ahead to see if it's NOT BETWEEN, NOT IN, or NOT LIKE
            // We need to save state to backtrack. Since we can't save ref struct state,
            // we'll check the next keyword by pattern.
            // For now, handle NOT BETWEEN/IN/LIKE by consuming NOT then checking.
            var savedPos = lexer.Current.Position;
            notBefore = true;
            Advance(ref lexer);

            if (IsAtEnd(ref lexer))
            {
                return left;
            }

            if (!IsKeyword(ref lexer, "BETWEEN") && !IsKeyword(ref lexer, "IN") && !IsKeyword(ref lexer, "LIKE"))
            {
                // It was NOT something_else, treat as a logical NOT on a comparison
                // This shouldn't normally happen in this position, fall through
                // by creating a NOT unary on whatever follows
                var rest = ParseComparison(ref lexer);
                return new SqlBinaryExpression(left, SqlBinaryOperator.And,
                    new SqlUnaryExpression(rest, SqlUnaryOperator.Not, Location.Create(1, 1, savedPos, savedPos)),
                    Location.Create(1, 1, savedPos, savedPos));
            }
        }

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "BETWEEN"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var low = ParseAddition(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AND"))
            {
                Advance(ref lexer);
            }

            var high = ParseAddition(ref lexer);
            return new SqlBetweenExpression(left, low, high, notBefore, Location.Create(1, 1, pos, pos));
        }

        // [NOT] IN (...)
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "IN"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
            {
                Advance(ref lexer);

                // Check if it's a subquery: IN (SELECT ...)
                if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SELECT"))
                {
                    var subquery = ParseSelect(ref lexer);
                    if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
                    {
                        Advance(ref lexer);
                    }

                    return new SqlInExpression(left, null, subquery, notBefore, Location.Create(1, 1, pos, pos));
                }

                // Value list
                var values = new List<SqlExpression>();
                if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
                {
                    values.Add(ParseExpression(ref lexer));
                    while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
                    {
                        Advance(ref lexer);
                        values.Add(ParseExpression(ref lexer));
                    }
                }
                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
                {
                    Advance(ref lexer);
                }

                return new SqlInExpression(left, values, null, notBefore, Location.Create(1, 1, pos, pos));
            }
            return new SqlInExpression(left, Array.Empty<SqlExpression>(), null, notBefore, Location.Create(1, 1, pos, pos));
        }

        // [NOT] LIKE pattern
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "LIKE"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var pattern = ParsePrimary(ref lexer);
            return new SqlLikeExpression(left, pattern, notBefore, Location.Create(1, 1, pos, pos));
        }

        // Standard comparison operators
        SqlBinaryOperator? op = GetComparisonOperator(ref lexer);
        if (op.HasValue)
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseAddition(ref lexer);
            return new SqlBinaryExpression(left, op.Value, right, Location.Create(1, 1, pos, pos));
        }

        return left;
    }

    private static SqlBinaryOperator? GetComparisonOperator(ref TokenLexer lexer)
    {
        return lexer.Current.Type switch
        {
            TokenType.Equals => SqlBinaryOperator.Equal,
            TokenType.NotEquals => SqlBinaryOperator.NotEqual,
            TokenType.LessThan => SqlBinaryOperator.LessThan,
            TokenType.GreaterThan => SqlBinaryOperator.GreaterThan,
            TokenType.LessEqual => SqlBinaryOperator.LessOrEqual,
            TokenType.GreaterEqual => SqlBinaryOperator.GreaterOrEqual,
            _ => null,
        };
    }

    private SqlExpression ParseAddition(ref TokenLexer lexer)
    {
        var left = ParseMultiplication(ref lexer);

        while (!IsAtEnd(ref lexer))
        {
            SqlBinaryOperator op;
            if (lexer.Current.Type == TokenType.Plus)
            {
                op = SqlBinaryOperator.Add;
            }
            else if (lexer.Current.Type == TokenType.Minus)
            {
                op = SqlBinaryOperator.Subtract;
            }
            else if (lexer.Current.Type == TokenType.Concat)
            {
                op = SqlBinaryOperator.Concat;
            }
            else
            {
                break;
            }

            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseMultiplication(ref lexer);
            left = new SqlBinaryExpression(left, op, right, Location.Create(1, 1, pos, pos));
        }

        return left;
    }

    private SqlExpression ParseMultiplication(ref TokenLexer lexer)
    {
        var left = ParseUnary(ref lexer);

        while (!IsAtEnd(ref lexer))
        {
            SqlBinaryOperator op;
            if (lexer.Current.Type == TokenType.Asterisk)
            {
                op = SqlBinaryOperator.Multiply;
            }
            else if (lexer.Current.Type == TokenType.Slash)
            {
                op = SqlBinaryOperator.Divide;
            }
            else if (lexer.Current.Type == TokenType.Percent)
            {
                op = SqlBinaryOperator.Modulo;
            }
            else
            {
                break;
            }

            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseUnary(ref lexer);
            left = new SqlBinaryExpression(left, op, right, Location.Create(1, 1, pos, pos));
        }

        return left;
    }

    private SqlExpression ParseUnary(ref TokenLexer lexer)
    {
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Minus)
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var operand = ParsePrimary(ref lexer);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Negate,
                Location.Create(1, 1, pos, pos));
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Tilde)
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var operand = ParsePrimary(ref lexer);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.BitwiseNot,
                Location.Create(1, 1, pos, pos));
        }

        return ParsePrimary(ref lexer);
    }

    private SqlExpression ParsePrimary(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer))
        {
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null,
                Location.Create(1, 1, 0, 0));
        }

        var pos = lexer.Current.Position;

        // Star (wildcard)
        if (lexer.Current.Type == TokenType.Asterisk)
        {
            Advance(ref lexer);
            return new SqlStarExpression(Location.Create(1, 1, pos, pos + 1));
        }

        // String literal: the AST carries the VALUE (quotes stripped, doubled
        // quotes unescaped), not the raw lexeme — executors and planners consume
        // it directly.
        if (lexer.Current.Type == TokenType.String)
        {
            var text = CurrentText(ref lexer);
            Advance(ref lexer);
            return new SqlLiteralExpression(UnquoteStringLiteral(text), SqlLiteralType.String,
                Location.Create(1, 1, pos, pos + text.Length));
        }

        // Integer literal
        if (lexer.Current.Type == TokenType.Integer)
        {
            var text = CurrentText(ref lexer);
            Advance(ref lexer);
            return new SqlLiteralExpression(text, SqlLiteralType.Integer,
                Location.Create(1, 1, pos, pos + text.Length));
        }

        // Float literal
        if (lexer.Current.Type == TokenType.Float)
        {
            var text = CurrentText(ref lexer);
            Advance(ref lexer);
            return new SqlLiteralExpression(text, SqlLiteralType.Float,
                Location.Create(1, 1, pos, pos + text.Length));
        }

        // Parameter
        if (lexer.Current.Type == TokenType.Parameter)
        {
            var name = CurrentText(ref lexer);
            Advance(ref lexer);
            return new SqlParameterExpression(name, Location.Create(1, 1, pos, pos + name.Length));
        }

        // NULL literal
        if (IsKeyword(ref lexer, "NULL"))
        {
            Advance(ref lexer);
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null,
                Location.Create(1, 1, pos, pos + 4));
        }

        // Boolean literals
        if (IsKeyword(ref lexer, "TRUE"))
        {
            Advance(ref lexer);
            return new SqlLiteralExpression("TRUE", SqlLiteralType.Boolean,
                Location.Create(1, 1, pos, pos + 4));
        }

        if (IsKeyword(ref lexer, "FALSE"))
        {
            Advance(ref lexer);
            return new SqlLiteralExpression("FALSE", SqlLiteralType.Boolean,
                Location.Create(1, 1, pos, pos + 5));
        }

        // EXISTS (subquery)
        if (IsKeyword(ref lexer, "EXISTS"))
        {
            return ParseExists(ref lexer, isNegated: false, pos);
        }

        // CASE expression
        if (IsKeyword(ref lexer, "CASE"))
        {
            return ParseCase(ref lexer);
        }

        // CAST expression
        if (IsKeywordOrFunction(ref lexer, "CAST"))
        {
            return ParseCast(ref lexer);
        }

        // Parenthesized expression or subquery
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);

            // Subquery: (SELECT ...)
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SELECT"))
            {
                var subSelect = ParseSelect(ref lexer);
                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
                {
                    Advance(ref lexer);
                }

                return new SqlSubqueryExpression(subSelect, Location.Create(1, 1, pos, pos));
            }

            // Parenthesized expression
            var inner = ParseExpression(ref lexer);
            if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
            {
                Advance(ref lexer);
            }

            return inner;
        }

        // Function call or column reference
        if (lexer.Current.Type == TokenType.Function)
        {
            return ParseFunctionCall(ref lexer);
        }

        // Identifier: could be column_ref (possibly dotted) or function call
        if (lexer.Current.Type == TokenType.Identifier ||
            lexer.Current.Type == TokenType.Keyword ||
            lexer.Current.Type == TokenType.QuotedIdentifier)
        {
            return ParseColumnRefOrFunction(ref lexer);
        }

        // Fallback: consume and return a null literal
        Advance(ref lexer);
        return new SqlLiteralExpression("NULL", SqlLiteralType.Null,
            Location.Create(1, 1, pos, pos));
    }

    private SqlExpression ParseColumnRefOrFunction(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        string first = CurrentText(ref lexer);
        Advance(ref lexer);

        // Check for function call: identifier(
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            return ParseFunctionCallArgs(ref lexer, first, pos);
        }

        // Check for dotted reference: a.b or a.b.c
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Dot)
        {
            Advance(ref lexer); // consume dot
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                string second = CurrentText(ref lexer);
                Advance(ref lexer);

                // Check for a.b.c
                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Dot)
                {
                    Advance(ref lexer);
                    if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                    {
                        string third = CurrentText(ref lexer);
                        Advance(ref lexer);
                        return new SqlColumnReferenceExpression(third, second, first,
                            Location.Create(1, 1, pos, pos));
                    }
                }

                // a.b — could be table.column or schema.table (interpret as table.column)
                return new SqlColumnReferenceExpression(second, first, null,
                    Location.Create(1, 1, pos, pos));
            }
        }

        // Simple identifier
        return new SqlColumnReferenceExpression(first, null, null,
            Location.Create(1, 1, pos, pos + first.Length));
    }

    private SqlExpression ParseFunctionCall(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        string name = CurrentText(ref lexer);
        Advance(ref lexer);

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            return ParseFunctionCallArgs(ref lexer, name, pos);
        }

        // If no parens, treat as column reference
        return new SqlColumnReferenceExpression(name, null, null,
            Location.Create(1, 1, pos, pos + name.Length));
    }

    private SqlExpression ParseFunctionCallArgs(ref TokenLexer lexer, string name, int pos)
    {
        Advance(ref lexer); // consume (

        var args = new List<SqlExpression>();

        if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
        {
            // Handle COUNT(*) and similar
            if (lexer.Current.Type == TokenType.Asterisk)
            {
                args.Add(new SqlStarExpression(Location.Create(1, 1, lexer.Current.Position, lexer.Current.Position + 1)));
                Advance(ref lexer);
            }
            else
            {
                args.Add(ParseExpression(ref lexer));
                while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    args.Add(ParseExpression(ref lexer));
                }
            }
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
        }

        return new SqlFunctionCallExpression(name, args, Location.Create(1, 1, pos, pos));
    }

    private SqlExpression ParseCase(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CASE

        // Simple CASE: CASE expr WHEN ... or Searched CASE: CASE WHEN ...
        SqlExpression? input = null;
        if (!IsAtEnd(ref lexer) && !IsKeyword(ref lexer, "WHEN"))
        {
            input = ParseExpression(ref lexer);
        }

        var whenClauses = new List<SqlWhenClause>();
        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHEN"))
        {
            Advance(ref lexer); // consume WHEN
            var condition = ParseExpression(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "THEN"))
            {
                Advance(ref lexer);
            }

            var result = ParseExpression(ref lexer);
            whenClauses.Add(new SqlWhenClause(condition, result));
        }

        SqlExpression? elseResult = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ELSE"))
        {
            Advance(ref lexer);
            elseResult = ParseExpression(ref lexer);
        }

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "END"))
        {
            Advance(ref lexer);
        }

        return new SqlCaseExpression(input, whenClauses, elseResult,
            Location.Create(1, 1, pos, pos));
    }

    private SqlExpression ParseCast(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CAST

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
        }

        var operand = ParseExpression(ref lexer);

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
        }

        // Parse type name (could be multi-word like VARCHAR(100))
        string targetType = string.Empty;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            targetType = CurrentText(ref lexer);
            Advance(ref lexer);

            // Handle parameterized types: VARCHAR(100)
            if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
            {
                targetType += "(";
                Advance(ref lexer);
                if (!IsAtEnd(ref lexer))
                {
                    targetType += CurrentText(ref lexer);
                    Advance(ref lexer);
                }
                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
                {
                    targetType += ")";
                    Advance(ref lexer);
                }
            }
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
        }

        return new SqlCastExpression(operand, targetType, Location.Create(1, 1, pos, pos));
    }

    private SqlExpression ParseExists(ref TokenLexer lexer, bool isNegated, int pos)
    {
        Advance(ref lexer); // consume EXISTS

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
        }

        SqlSelectExpression? subquery = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SELECT"))
        {
            subquery = ParseSelect(ref lexer);
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
        }

        subquery ??= new SqlSelectExpression(
            Array.Empty<SqlSelectColumn>(), null, Array.Empty<SqlJoinClause>(),
            null, Array.Empty<SqlExpression>(), null, Array.Empty<SqlOrderByColumn>(),
            null, null, false, null, null);

        return new SqlExistsExpression(subquery, isNegated, Location.Create(1, 1, pos, pos));
    }
}
