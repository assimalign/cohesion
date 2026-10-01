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
    //   ParseComparison         → ParseComparisonCore
    //                            (an infix ~ after IS NULL, IN, LIKE or BETWEEN reports COHDBL001)
    //   ParseComparisonCore     → ParseAddition ((=|<>|<|>|<=|>=) ParseAddition
    //                            | IS [NOT] NULL
    //                            | [NOT] BETWEEN ... AND ...
    //                            | [NOT] IN (...)
    //                            | [NOT] LIKE ...)?
    //                            (op ANY|SOME|ALL (...) reports COHDBL001 and is skipped)
    //   ParseAddition           → ParseMultiplication ((+|-||) ParseMultiplication)*
    //                            (an infix ~, ~*, ~~, !~ ... here reports COHDBL001)
    //   ParseMultiplication     → ParseUnary ((*|/|%) ParseUnary)*
    //   ParseUnary              → (-|+|~) ParseUnary | ParseCollate
    //                            (a prefix ~ reports COHDBL001; a + directly before a
    //                            numeric literal is part of the literal, see ParsePrimary)
    //   ParseCollate            → ParsePrimary (COLLATE name)*
    //   ParsePrimary            → literal | column_ref | param | function(...)
    //                            | (expr) | (SELECT ...) | CASE | CAST | EXISTS | *
    //                            (a ~ here, as in LIKE ~'x' or DEFAULT ~1, goes to ParseUnary)
    //
    // An operand a node encloses is parsed through ParseOperand, and a node built over an
    // operand parsed before it is checked with Nest: together they bound the tree at
    // MaximumExpressionDepth levels (SqlQueryParser.Nesting.cs, #1151).

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
            var right = ParseOperand(ref lexer, OperandRule.And);
            left = Nest(ref lexer, new SqlBinaryExpression(left, SqlBinaryOperator.Or, right,
                Location.Create(1, 1, pos, pos)));
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
            var right = ParseOperand(ref lexer, OperandRule.Not);
            left = Nest(ref lexer, new SqlBinaryExpression(left, SqlBinaryOperator.And, right,
                Location.Create(1, 1, pos, pos)));
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

            var operand = ParseOperand(ref lexer, OperandRule.Not);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Not,
                Location.Create(1, 1, pos, pos));
        }

        return ParseComparison(ref lexer);
    }

    private SqlExpression ParseComparison(ref TokenLexer lexer)
    {
        var result = ParseComparisonCore(ref lexer);

        // IS NULL, IN (...), LIKE and BETWEEN complete without returning to the additive
        // loop, so an infix ~ after them fell to the leftover-token check with a generic
        // SQL0003 instead of naming the operator (#1101).
        while (IsInfixTilde(ref lexer))
        {
            RejectInfixTilde(ref lexer, afterPredicate: true);
        }

        return result;
    }

    private SqlExpression ParseComparisonCore(ref TokenLexer lexer)
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
            else
            {
                RejectIsPredicate(ref lexer, pos, negated);
            }
            return Nest(ref lexer, new SqlIsNullExpression(left, negated, Location.Create(1, 1, pos, pos)));
        }

        // [NOT] BETWEEN ... AND ...
        bool notBefore = false;
        if (IsKeyword(ref lexer, "NOT"))
        {
            notBefore = true;
            Advance(ref lexer);

            if (!IsKeyword(ref lexer, "BETWEEN") && !IsKeyword(ref lexer, "IN") && !IsKeyword(ref lexer, "LIKE"))
            {
                // x NOT y is not a predicate. It used to parse as x AND NOT y, so SQLite's
                // postfix a NOT NULL filtered out every row and flag NOT FALSE matched only
                // TRUE rows (#1068). The token after NOT stays in place, so the leftover
                // check finds it already reported.
                AddSyntaxDiagnostic(ref lexer, IsAtEnd(ref lexer)
                    ? "Expected BETWEEN, IN or LIKE after NOT before the end of the statement."
                    : $"Expected BETWEEN, IN or LIKE after NOT but found {DescribeToken(ref lexer)}; write IS NOT NULL, or put NOT before the whole predicate.");
                return left;
            }
        }

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "BETWEEN"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var low = ParseOperand(ref lexer, OperandRule.Addition);
            ExpectKeyword(ref lexer, "AND", "AND between the BETWEEN bounds");

            var high = ParseOperand(ref lexer, OperandRule.Addition);
            return Nest(ref lexer, new SqlBetweenExpression(left, low, high, notBefore, Location.Create(1, 1, pos, pos)));
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
                    var subquery = ParseSubquery(ref lexer);
                    Expect(ref lexer, TokenType.RightParen, "')' after the IN subquery");

                    return Nest(ref lexer, new SqlInExpression(left, null, subquery, notBefore, Location.Create(1, 1, pos, pos)));
                }

                // Value list
                var values = new List<SqlExpression>();
                if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
                {
                    values.Add(ParseOperand(ref lexer, OperandRule.Expression));
                    while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
                    {
                        Advance(ref lexer);
                        values.Add(ParseOperand(ref lexer, OperandRule.Expression));
                    }
                }
                Expect(ref lexer, TokenType.RightParen, "')' after the IN list");

                return Nest(ref lexer, new SqlInExpression(left, values, null, notBefore, Location.Create(1, 1, pos, pos)));
            }

            AddExpectedDiagnostic(ref lexer, "'(' after IN");
            return Nest(ref lexer, new SqlInExpression(left, Array.Empty<SqlExpression>(), null, notBefore, Location.Create(1, 1, pos, pos)));
        }

        // [NOT] LIKE pattern
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "LIKE"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var pattern = ParseOperand(ref lexer, OperandRule.Collate);
            if (IsWord(ref lexer, "ESCAPE"))
            {
                // Without this check the escape clause was left behind, and in a select
                // list ESCAPE even became the column alias (#1068).
                AddSyntaxDiagnostic(ref lexer,
                    "LIKE ... ESCAPE is not supported by the SQL surface; '%' and '_' in a LIKE pattern are always wildcards.");
                Advance(ref lexer);
                if (CanStartOperand(ref lexer))
                {
                    ParseOperand(ref lexer, OperandRule.Collate); // recover past the escape character
                }
            }
            return Nest(ref lexer, new SqlLikeExpression(left, pattern, notBefore, Location.Create(1, 1, pos, pos)));
        }

        // Standard comparison operators
        SqlBinaryOperator? op = GetComparisonOperator(ref lexer);
        if (op.HasValue)
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = SkipQuantifiedComparison(ref lexer) ?? ParseOperand(ref lexer, OperandRule.Addition);
            return Nest(ref lexer, new SqlBinaryExpression(left, op.Value, right, Location.Create(1, 1, pos, pos)));
        }

        return left;
    }

    /// <summary>
    /// Skips the quantified operand of <c>op ANY|SOME|ALL (subquery)</c> after reporting the
    /// construct once. It used to parse as a call to a function named ANY whose argument was a
    /// SELECT, so it also reported "Expected ')' after the ANY arguments" (#1101).
    /// </summary>
    /// <returns>A placeholder that never executes, or <see langword="null"/> when no quantifier is current.</returns>
    private SqlLiteralExpression? SkipQuantifiedComparison(ref TokenLexer lexer)
    {
        if (!(IsWord(ref lexer, "ANY") || IsWord(ref lexer, "SOME") || IsWord(ref lexer, "ALL")) ||
            !TryPeekToken(lexer, out string next, out _) || next != "(")
        {
            return null;
        }

        int start = lexer.Current.Position;
        string quantifier = CurrentText(ref lexer).ToUpperInvariant();
        RequireSkippedConstruct(start, start + quantifier.Length, $"{quantifier} quantified comparison");
        Advance(ref lexer);
        SkipParenthesized(ref lexer);
        return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, start));
    }

    /// <summary>
    /// Rejects an IS predicate other than <c>IS [NOT] NULL</c>. The ISO boolean test
    /// (<c>IS [NOT] TRUE|FALSE|UNKNOWN</c>) and distinct predicate
    /// (<c>IS [NOT] DISTINCT FROM</c>) used to parse as <c>IS NULL</c> with their operand
    /// left behind, so <c>DELETE ... WHERE flag IS TRUE</c> deleted the NULL rows (#1068).
    /// The rest of the predicate is consumed for recovery.
    /// </summary>
    private void RejectIsPredicate(ref TokenLexer lexer, int start, bool negated)
    {
        string form = negated ? "IS NOT" : "IS";
        if (IsWord(ref lexer, "TRUE") || IsWord(ref lexer, "FALSE") || IsWord(ref lexer, "UNKNOWN"))
        {
            string test = CurrentText(ref lexer).ToUpperInvariant();
            AddSyntaxDiagnostic(start, lexer.Current.Position + lexer.Current.Value.Length,
                $"The {form} {test} predicate is not supported by the SQL surface; only IS [NOT] NULL is.");
            Advance(ref lexer);
            return;
        }

        if (IsKeyword(ref lexer, "DISTINCT"))
        {
            int end = lexer.Current.Position + lexer.Current.Value.Length;
            Advance(ref lexer);
            if (IsKeyword(ref lexer, "FROM"))
            {
                end = lexer.Current.Position + lexer.Current.Value.Length;
                Advance(ref lexer);
            }

            AddSyntaxDiagnostic(start, end,
                $"The {form} DISTINCT FROM predicate is not supported by the SQL surface; only IS [NOT] NULL is.");
            if (CanStartOperand(ref lexer))
            {
                ParseOperand(ref lexer, OperandRule.Addition); // recover past the comparand
            }
            return;
        }

        AddSyntaxDiagnostic(ref lexer, $"Expected NULL after {form}.");
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
            else if (IsInfixTilde(ref lexer))
            {
                RejectInfixTilde(ref lexer, afterPredicate: false);
                continue;
            }
            else
            {
                break;
            }

            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseOperand(ref lexer, OperandRule.Multiplication);
            left = Nest(ref lexer, new SqlBinaryExpression(left, op, right, Location.Create(1, 1, pos, pos)));
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
            var right = ParseOperand(ref lexer, OperandRule.Unary);
            left = Nest(ref lexer, new SqlBinaryExpression(left, op, right, Location.Create(1, 1, pos, pos)));
        }

        return left;
    }

    // The operand of a sign is itself a unary expression, so - -1 is 1 and + -a is -a, as in
    // ISO SQL.
    private SqlExpression ParseUnary(ref TokenLexer lexer)
    {
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Minus)
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var operand = ParseOperand(ref lexer, OperandRule.Unary);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Negate,
                Location.Create(1, 1, pos, pos));
        }

        // ISO unary plus. Directly before a numeric literal the sign stays part of the
        // literal (ParsePrimary folds it), so ORDER BY +1 remains the ordinal 1; on any
        // other operand it is an operator. +a and +(1 + 2) used to be parse errors because
        // the AST had no unary plus (#1068).
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Plus && !IsSignedNumericLiteral(lexer))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);
            var operand = ParseOperand(ref lexer, OperandRule.Unary);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Plus,
                Location.Create(1, 1, pos, pos));
        }

        // ~ is recognized but outside the executable scalar subset. The evaluator used to
        // return NULL for a NULL operand and throw per row otherwise, so SELECT ~NULL and
        // ~ over an empty table succeeded (#1101). The node is kept for recovery only; the
        // error means it never executes.
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Tilde)
        {
            // A run of prefix operators, ~ ~a, is one construct and one diagnostic.
            var pos = lexer.Current.Position;
            int end = pos + 1;
            while (Advance(ref lexer) && lexer.Current.Type == TokenType.Tilde)
            {
                end = lexer.Current.Position + 1;
            }

            AddUnsupportedSurfaceDiagnostic(pos, end,
                "The prefix ~ operator (bitwise NOT) is not supported by the SQL surface.");
            var operand = ParseOperand(ref lexer, OperandRule.Unary);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.BitwiseNot,
                Location.Create(1, 1, pos, pos));
        }

        return ParseCollate(ref lexer);
    }

    /// <summary>
    /// Whether the current <c>+</c> directly precedes a numeric literal, comments aside. Such a
    /// sign is part of the literal rather than a unary plus, which keeps <c>ORDER BY +1</c> an
    /// ordinal and gives <c>+1</c> the same tree as <c>1</c>.
    /// </summary>
    private static bool IsSignedNumericLiteral(TokenLexer lexer)
        => lexer.Current.Type == TokenType.Plus && AdvancePastComments(ref lexer) &&
           lexer.Current.Type is TokenType.Integer or TokenType.Float;

    /// <summary>
    /// Whether an infix <c>~</c> operator starts at the current token: <c>~</c> itself, or
    /// <c>!</c> directly followed by <c>~</c> (PostgreSQL's negated <c>!~</c> forms).
    /// </summary>
    private static bool IsInfixTilde(ref TokenLexer lexer)
    {
        if (lexer.Current.Type == TokenType.Tilde)
        {
            return true;
        }

        var next = lexer;
        return lexer.Current.Type == TokenType.Bang && next.MoveNext() &&
               next.Current.Type == TokenType.Tilde && next.Current.Position == lexer.Current.Position + 1;
    }

    /// <summary>
    /// Rejects an infix <c>~</c>, PostgreSQL's regular-expression match, and its family
    /// <c>~*</c>, <c>!~</c>, <c>!~*</c> and the LIKE forms <c>~~</c>, <c>~~*</c>, <c>!~~</c>,
    /// <c>!~~*</c>, each as one operator. The dialect has no rule for them, so they used to
    /// fall to the leftover-token check with a generic message, or report twice. It is checked
    /// wherever an additive operator may follow an operand, and after a comparison predicate.
    /// The right operand is parsed for recovery and no node is built (#1101).
    /// </summary>
    /// <param name="lexer">The lexer, at the operator.</param>
    /// <param name="afterPredicate">Whether the operator follows a whole comparison predicate.</param>
    private void RejectInfixTilde(ref TokenLexer lexer, bool afterPredicate)
    {
        int start = lexer.Current.Position;
        if (lexer.Current.Type == TokenType.Bang)
        {
            Advance(ref lexer); // to the adjacent ~
        }

        int end = lexer.Current.Position + 1;
        Advance(ref lexer);
        if (lexer.Current.Type == TokenType.Tilde && lexer.Current.Position == end)
        {
            end++;
            Advance(ref lexer);
        }
        if (lexer.Current.Type == TokenType.Asterisk && lexer.Current.Position == end)
        {
            end++;
            Advance(ref lexer);
        }

        string spelling = _sourceText.Substring(start, end - start);
        string meaning = spelling.Contains("~~", StringComparison.Ordinal) ? "LIKE match" : "regular-expression match";
        AddUnsupportedSurfaceDiagnostic(start, end,
            $"The infix {spelling} operator ({meaning}) is not supported by the SQL surface.");
        if (CanStartOperand(ref lexer))
        {
            ParseOperand(ref lexer, afterPredicate ? OperandRule.Addition : OperandRule.Multiplication);
        }
    }

    private SqlExpression ParsePrimary(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;

        if (IsAtEnd(ref lexer))
        {
            return MissingExpression(ref lexer, pos);
        }

        // Preserve a signed numeric token as a literal. ORDER BY binding must
        // distinguish +1 from a larger constant expression such as +1 + 1.
        if (lexer.Current.Type == TokenType.Plus)
        {
            if (IsSignedNumericLiteral(lexer))
            {
                Advance(ref lexer);
                string value = CurrentText(ref lexer);
                var type = lexer.Current.Type == TokenType.Integer ? SqlLiteralType.Integer : SqlLiteralType.Float;
                int end = lexer.Current.Position + lexer.Current.Value.Length;
                Advance(ref lexer);
                return new SqlLiteralExpression(value, type, Location.Create(1, 1, pos, end));
            }
        }

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

            // Subquery: (SELECT ...). Its parentheses are its syntax, not grouping: the
            // subquery is a node, which ParseSubquery counts as a level of the tree.
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SELECT"))
            {
                var subSelect = ParseSubquery(ref lexer);
                Expect(ref lexer, TokenType.RightParen, "')' after the subquery");

                return new SqlSubqueryExpression(subSelect, Location.Create(1, 1, pos, pos));
            }

            // Parenthesized expression: a level of recursion, though not of the tree.
            if (!TryEnterParenthesis(ref lexer, pos))
            {
                return NestingPlaceholder(pos);
            }

            var inner = ParseExpression(ref lexer);
            _parenthesisDepth--;
            Expect(ref lexer, TokenType.RightParen, "')'");

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

        // Rules that take a primary directly, such as the LIKE pattern or a column DEFAULT,
        // reported ~ as a missing expression instead of naming the operator (#1101).
        if (lexer.Current.Type == TokenType.Tilde)
        {
            return ParseUnary(ref lexer);
        }

        return MissingExpression(ref lexer, pos);
    }

    /// <summary>
    /// Reports a token that cannot start an expression — a ';', ')', the end of the
    /// text, or ':' in an unsupported <c>:name</c> parameter — and returns a NULL
    /// placeholder so parsing stays total. The token is left in place: callers resume at
    /// it and the statement-level leftover check (#1068) finds it already reported.
    /// Silently consuming it here once turned <c>WHERE id = :id</c> into
    /// <c>WHERE id = NULL</c> and <c>SET a = :a</c> into an unconditional NULL write.
    /// </summary>
    private SqlLiteralExpression MissingExpression(ref TokenLexer lexer, int position)
    {
        if (!HasErrorAt(position))
        {
            AddSyntaxDiagnostic(ref lexer, IsAtEnd(ref lexer)
                ? "Expected an expression before the end of the statement."
                : $"Expected an expression but found {DescribeToken(ref lexer)}.");
        }

        return new SqlLiteralExpression("NULL", SqlLiteralType.Null,
            Location.Create(1, 1, position, position));
    }

    private SqlExpression ParseColumnRefOrFunction(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        string first = CurrentIdentifierText(ref lexer);
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
            if (lexer.Current.Type == TokenType.Asterisk)
            {
                return ParseUnsupportedQualifiedStar(ref lexer, pos);
            }
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                string second = CurrentIdentifierText(ref lexer);
                Advance(ref lexer);

                // Check for a.b.c
                if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Dot)
                {
                    Advance(ref lexer);
                    if (lexer.Current.Type == TokenType.Asterisk)
                    {
                        return ParseUnsupportedQualifiedStar(ref lexer, pos);
                    }
                    if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                    {
                        string third = CurrentIdentifierText(ref lexer);
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

    private SqlStarExpression ParseUnsupportedQualifiedStar(ref TokenLexer lexer, int start)
    {
        int end = lexer.Current.Position + 1;
        AddUnsupportedSurfaceDiagnostic(start, end,
            "Qualified SQL wildcard projections are not supported; select explicit columns or use unqualified *.");
        Advance(ref lexer);
        return new SqlStarExpression(Location.Create(1, 1, start, end));
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

        if (IsAggregateFunction(name) && (IsKeyword(ref lexer, "DISTINCT") || IsKeyword(ref lexer, "ALL")))
        {
            string modifier = CurrentText(ref lexer).ToUpperInvariant();
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position, lexer.Current.Position + modifier.Length,
                $"The {modifier} modifier inside SQL aggregate functions is not supported.");
            Advance(ref lexer);
        }

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
                args.Add(ParseOperand(ref lexer, OperandRule.Expression));
                while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    args.Add(ParseOperand(ref lexer, OperandRule.Expression));
                }
            }
        }

        if (IsAggregateFunction(name) && IsKeyword(ref lexer, "ORDER"))
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length,
                "ORDER BY inside SQL aggregate functions is not supported.");
        }

        Expect(ref lexer, TokenType.RightParen, $"')' after the {name} arguments");
        SkipCallExtensions(ref lexer);

        return new SqlFunctionCallExpression(name, args, Location.Create(1, 1, pos, pos));
    }

    /// <summary>
    /// Skips <c>WITHIN GROUP (...)</c> and <c>FILTER (...)</c> after a call, reporting each once.
    /// <c>WITHIN</c> used to become the column alias and <c>GROUP (ORDER BY ...)</c> a malformed
    /// GROUP BY, so the construct also reported two SQL0003 diagnostics (#1101).
    /// </summary>
    private void SkipCallExtensions(ref TokenLexer lexer)
    {
        while (true)
        {
            int start = lexer.Current.Position;
            if (IsWord(ref lexer, "WITHIN") && TryPeekToken(lexer, out string group, out int groupEnd) &&
                group.Equals("GROUP", StringComparison.OrdinalIgnoreCase))
            {
                RequireSkippedConstruct(start, groupEnd, "WITHIN GROUP");
                Advance(ref lexer);
                Advance(ref lexer);
            }
            else if (IsWord(ref lexer, "FILTER") && TryPeekToken(lexer, out string next, out _) && next == "(")
            {
                RequireSkippedConstruct(start, start + lexer.Current.Value.Length, "FILTER");
                Advance(ref lexer);
            }
            else
            {
                return;
            }

            if (lexer.Current.Type == TokenType.LeftParen)
            {
                SkipParenthesized(ref lexer);
            }
        }
    }

    private SqlExpression ParseCase(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume CASE

        // Simple CASE: CASE expr WHEN ... or Searched CASE: CASE WHEN ...
        SqlExpression? input = null;
        if (!IsAtEnd(ref lexer) && !IsKeyword(ref lexer, "WHEN"))
        {
            input = ParseOperand(ref lexer, OperandRule.Expression);
        }

        var whenClauses = new List<SqlWhenClause>();
        if (!IsKeyword(ref lexer, "WHEN"))
        {
            AddExpectedDiagnostic(ref lexer, "WHEN in the CASE expression");
        }

        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHEN"))
        {
            Advance(ref lexer); // consume WHEN
            var condition = ParseOperand(ref lexer, OperandRule.Expression);
            ExpectKeyword(ref lexer, "THEN", "THEN after the WHEN condition");

            var result = ParseOperand(ref lexer, OperandRule.Expression);
            whenClauses.Add(new SqlWhenClause(condition, result));
        }

        SqlExpression? elseResult = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ELSE"))
        {
            Advance(ref lexer);
            elseResult = ParseOperand(ref lexer, OperandRule.Expression);
        }

        ExpectKeyword(ref lexer, "END", "END to close the CASE expression");

        return new SqlCaseExpression(input, whenClauses, elseResult,
            Location.Create(1, 1, pos, pos));
    }

    private SqlExpression ParseExists(ref TokenLexer lexer, bool isNegated, int pos)
    {
        Advance(ref lexer); // consume EXISTS

        SqlSelectExpression? subquery = null;
        if (Expect(ref lexer, TokenType.LeftParen, "'(' after EXISTS"))
        {
            if (IsKeyword(ref lexer, "SELECT"))
            {
                subquery = ParseSubquery(ref lexer);
            }
            else
            {
                AddExpectedDiagnostic(ref lexer, "a SELECT subquery after EXISTS (");
            }

            Expect(ref lexer, TokenType.RightParen, "')' after the EXISTS subquery");
        }

        subquery ??= new SqlSelectExpression(
            Array.Empty<SqlSelectColumn>(), null, Array.Empty<SqlJoinClause>(),
            null, Array.Empty<SqlExpression>(), null, Array.Empty<SqlOrderByColumn>(),
            null, null, false, null, null);

        return new SqlExistsExpression(subquery, isNegated, Location.Create(1, 1, pos, pos));
    }
}
