using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

public sealed partial class SqlQueryParser
{
    // ── Expression parsing (recursive descent with precedence) ─────────
    //
    //   ParseExpression         → ParseOr
    //   ParseOr                 → ParseAnd (OR ParseAnd)*      one n-ary node per chain
    //   ParseAnd                → ParseNot (AND ParseNot)*     one n-ary node per chain
    //   ParseNot                 → NOT ParseNot | ParseComparison
    //   ParseComparison          → IS (low), comparison, BETWEEN/IN/LIKE (high), nonassociative
    //   ParseConcat              → ParseAddition (|| ParseAddition)*
    //   ParseAddition            → ParseMultiplication ((+|-) ParseMultiplication)*
    //   ParseMultiplication     → ParseCollate ((*|/|%) ParseCollate)*
    //   ParseUnary              → (-|+|~) ParseUnary | ParsePrimary
    //                            (a prefix ~ reports COHDBL001; a + directly before a
    //                            numeric literal is part of the literal, see ParsePrimary)
    //   ParseCollate            → ParseUnary (COLLATE name)*
    //   ParsePrimary            → literal | column_ref | param | function(...)
    //                            | (expr) | (SELECT ...) | CASE | CAST | EXISTS | *
    //                            (a ~ here, as in LIKE ~'x' or DEFAULT ~1, goes to ParseUnary)
    //
    // An operand a node encloses is parsed through ParseOperand, and a node built over an
    // operand parsed before it is checked with Nest (an AND/OR chain also with TryOpenChain):
    // together they bound the tree at the configured nesting limit (SqlQueryParser.Nesting.cs,
    // #1151).

    private SqlExpression ParseExpression(ref TokenLexer lexer)
    {
        if (!_columnDefault) { return ParseOr(ref lexer); }
        // A full expression inside a DEFAULT's grouping parentheses or call
        // arguments retains expression COLLATE; the outer COLLATE belongs to the column.
        _columnDefault = false;
        try { return ParseOr(ref lexer); }
        finally { _columnDefault = true; }
    }

    private SqlExpression ParseOr(ref TokenLexer lexer)
    {
        var first = ParseAnd(ref lexer);
        return !IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OR")
            ? ParseLogicalChain(ref lexer, first, SqlLogicalOperator.Or)
            : first;
    }

    private SqlExpression ParseAnd(ref TokenLexer lexer)
    {
        var first = ParseNot(ref lexer);
        return !IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AND")
            ? ParseLogicalChain(ref lexer, first, SqlLogicalOperator.And)
            : first;
    }

    /// <summary>
    /// Parses the rest of an <c>AND</c> or <c>OR</c> chain into one n-ary node, PostgreSQL's
    /// shape (#1151): the chain is one level of the tree however many terms it has, so a
    /// 10,000-term predicate parses, and every walker iterates its terms instead of recursing
    /// once per term. Binary nodes made such a chain as deep as it was long.
    /// </summary>
    /// <remarks>
    /// A parenthesized chain of the same operator that opens the chain merges into it, so
    /// <c>(a AND b) AND c</c> is the node <c>a AND b AND c</c> builds, exactly as left
    /// associativity read it before; one in a later position, <c>a AND (b AND c)</c>, stays a
    /// nested node. The tree is therefore the one the binary form had, with each run of links
    /// collapsed, and the canonical text the renderer stores for it is unchanged.
    /// <para>
    /// Parsing a chain costs its own terms only. The chain takes over the operand list of the
    /// chain it absorbs rather than copying it, and tracks the deepest operand as it adds each
    /// one, so <c>((X AND t) AND t) ... AND t</c> is linear in its length however many
    /// parentheses wrap <c>X</c>.
    /// </para>
    /// </remarks>
    /// <param name="lexer">The lexer, at the chain's first operator.</param>
    /// <param name="first">The first operand, parsed before the chain was known.</param>
    /// <param name="op">The chain's operator.</param>
    private SqlExpression ParseLogicalChain(ref TokenLexer lexer, SqlExpression first, SqlLogicalOperator op)
    {
        int position = lexer.Current.Position;
        var opening = first is SqlLogicalExpression group && group.Operator == op ? group : null;

        // The deepest operand so far: the first, or the deepest one of the chain it absorbs.
        int operandDepth = opening is null ? first.Depth : opening.Depth - 1;
        if (!TryOpenChain(ref lexer, operandDepth))
        {
            return first;
        }

        // The absorbed chain is dropped, so its list is taken over rather than copied: a copy
        // per pair of parentheses made the parse cost terms times parentheses.
        List<SqlExpression> operands = opening is null ? [first] : opening.DetachOperands();

        string keyword = op == SqlLogicalOperator.And ? "AND" : "OR";
        var rule = op == SqlLogicalOperator.And ? OperandRule.Not : OperandRule.And;
        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, keyword))
        {
            Advance(ref lexer);
            var operand = ParseOperand(ref lexer, rule);
            operands.Add(operand);
            operandDepth = Math.Max(operandDepth, operand.Depth);
        }

        return Nest(ref lexer, new SqlLogicalExpression(op, operands, operandDepth, Location.Create(1, 1, position, position)));
    }

    private SqlExpression ParseNot(ref TokenLexer lexer)
    {
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "NOT"))
        {
            var pos = lexer.Current.Position;
            Advance(ref lexer);

            // NOT binds below predicates, including those following EXISTS (gram.y:846-853).
            bool startsExists = IsWord(ref lexer, "EXISTS");
            // The compact NOT EXISTS leaf has the same depth as EXISTS. If a following
            // predicate requires a separate NOT node, Nest charges that level afterwards.
            var operand = startsExists ? ParseComparison(ref lexer) : ParseOperand(ref lexer, OperandRule.Not);
            if (startsExists && operand is SqlExistsExpression { IsNegated: false } exists)
            {
                return new SqlExistsExpression(exists.Subquery, true, Location.Create(1, 1, pos, pos));
            }

            return Nest(ref lexer, new SqlUnaryExpression(operand, SqlUnaryOperator.Not,
                Location.Create(1, 1, pos, pos)));
        }

        return ParseComparison(ref lexer);
    }

    // PostgreSQL gram.y:847-853: IS < comparison < BETWEEN/IN/LIKE < ESCAPE.
    // Postfix IS tests may be followed by higher-precedence operators; binary predicates
    // at the same rung are nonassociative, except completed IN forms. Every recursive
    // operand and loop node is bounded.
    private SqlExpression ParseComparison(ref TokenLexer lexer) => ParsePredicates(ref lexer, 1);

    private SqlExpression ParsePredicates(ref TokenLexer lexer, int minimum, bool restricted = false)
    {
        var left = !restricted && IsWord(ref lexer, "NOT") ? ParseNot(ref lexer) : ParseConcat(ref lexer);
        int previous = 0;
        while (!IsAtEnd(ref lexer))
        {
            if (IsInfixTilde(ref lexer))
            {
                RejectInfixTilde(ref lexer, afterPredicate: true);
                continue;
            }
            int pos = lexer.Current.Position;
            if (IsWord(ref lexer, "ISNULL") || IsWord(ref lexer, "NOTNULL"))
            {
                RequireSkippedConstruct(pos, CurrentTokenEnd(ref lexer), CurrentText(ref lexer).ToUpperInvariant());
                Advance(ref lexer);
                continue;
            }
            if (IsWord(ref lexer, "IS"))
            {
                if (minimum > 1) { break; }
                if (restricted)
                {
                    var next = lexer;
                    while (next.MoveNext() && next.Current.Type == TokenType.Comment) { }
                    if (IsWord(ref next, "NOT")) { while (next.MoveNext() && next.Current.Type == TokenType.Comment) { } }
                    if (!IsWord(ref next, "DISTINCT")) { break; }
                }
                if (previous == 1) { AddSyntaxDiagnostic(ref lexer, "IS DISTINCT FROM predicates require parentheses to chain."); SkipQueryRemainder(ref lexer); break; }
                Advance(ref lexer);
                bool negated = IsWord(ref lexer, "NOT");
                if (negated) { Advance(ref lexer); }
                if (IsWord(ref lexer, "NULL"))
                {
                    Advance(ref lexer);
                    left = Nest(ref lexer, new SqlIsNullExpression(left, negated, Location.Create(1, 1, pos, pos)));
                    previous = 0;
                    continue;
                }
                if (IsWord(ref lexer, "TRUE") || IsWord(ref lexer, "FALSE") || IsWord(ref lexer, "UNKNOWN"))
                {
                    var truth = IsWord(ref lexer, "TRUE") ? SqlTruthValue.True :
                        IsWord(ref lexer, "FALSE") ? SqlTruthValue.False : SqlTruthValue.Unknown;
                    Advance(ref lexer);
                    left = Nest(ref lexer, new SqlBooleanTestExpression(left, truth, negated, Location.Create(1, 1, pos, pos)));
                    previous = 0;
                    continue;
                }
                if (IsWord(ref lexer, "DISTINCT"))
                {
                    Advance(ref lexer);
                    ExpectKeyword(ref lexer, "FROM", "FROM after IS [NOT] DISTINCT");
                    var right = ParseOperand(ref lexer, restricted ? OperandRule.RestrictedComparison : OperandRule.ComparisonOperand);
                    left = Nest(ref lexer, new SqlDistinctExpression(left, right, negated, Location.Create(1, 1, pos, pos)));
                    previous = 1;
                    continue;
                }
                if (IsWord(ref lexer, "JSON") || IsWord(ref lexer, "OF") || IsWord(ref lexer, "NORMALIZED"))
                {
                    RejectPredicateOperator(ref lexer, pos, "IS " + (negated ? "NOT " : "") + CurrentText(ref lexer).ToUpperInvariant());
                    break;
                }
                AddExpectedDiagnostic(ref lexer, "NULL, TRUE, FALSE, UNKNOWN or DISTINCT FROM after IS [NOT]");
                break;
            }

            // A completed postfix form has no right operand to absorb a following
            // tighter operator (NULL IS NULL || 'x', or a IN (1) + 1). Resume that
            // operator over the completed left node, as PostgreSQL's a_expr does.
            var arithmetic = lexer.Current.Type switch
            {
                TokenType.Concat => ((SqlBinaryOperator?)SqlBinaryOperator.Concat, OperandRule.Addition),
                TokenType.Plus => (SqlBinaryOperator.Add, OperandRule.Multiplication),
                TokenType.Minus => (SqlBinaryOperator.Subtract, OperandRule.Multiplication),
                TokenType.Asterisk => (SqlBinaryOperator.Multiply, OperandRule.Collate),
                TokenType.Slash => (SqlBinaryOperator.Divide, OperandRule.Collate),
                TokenType.Percent => (SqlBinaryOperator.Modulo, OperandRule.Collate),
                _ => ((SqlBinaryOperator?)null, OperandRule.Unary),
            };
            if (arithmetic.Item1 is { } arithmeticOperator)
            {
                Advance(ref lexer);
                var right = ParseOperand(ref lexer, arithmetic.Item2);
                left = Nest(ref lexer, new SqlBinaryExpression(left, arithmeticOperator, right, Location.Create(1, 1, pos, pos)));
                previous = 0;
                continue;
            }
            if (!_columnDefault && IsWord(ref lexer, "COLLATE"))
            {
                Advance(ref lexer);
                string name = ParseCollationName(ref lexer);
                left = Nest(ref lexer, new SqlCollateExpression(left, name, Location.Create(1, 1, pos, _lastTokenEnd)));
                previous = 0;
                continue;
            }

            var op = GetComparisonOperator(ref lexer);
            if (op.HasValue)
            {
                // << and >> are unsupported operators, rather than two comparisons.
                if ((lexer.Current.Type is TokenType.LessThan or TokenType.GreaterThan) &&
                    TryPeekToken(lexer, out string next, out int end) && next == CurrentText(ref lexer))
                {
                    RejectPredicateOperator(ref lexer, pos, next + next);
                    break;
                }
                if (minimum > 2) { break; }
                if (previous == 2) { AddSyntaxDiagnostic(ref lexer, "Comparison operators require parentheses to chain."); SkipQueryRemainder(ref lexer); break; }
                Advance(ref lexer);
                var right = SkipQuantifiedComparison(ref lexer) ?? ParseOperand(ref lexer, restricted ? OperandRule.RestrictedPredicate : OperandRule.PredicateOperand);
                left = Nest(ref lexer, new SqlBinaryExpression(left, op.Value, right, Location.Create(1, 1, pos, pos)));
                previous = 2;
                continue;
            }

            if (lexer.Current.Type is TokenType.Ampersand or TokenType.Pipe or TokenType.ColonColon)
            {
                RejectPredicateOperator(ref lexer, pos, lexer.Current.Type == TokenType.ColonColon ? ":: cast; use CAST(x AS type)" : CurrentText(ref lexer));
                break;
            }
            bool notBefore = IsWord(ref lexer, "NOT");
            bool predicate = notBefore || IsWord(ref lexer, "BETWEEN") || IsWord(ref lexer, "IN") ||
                IsWord(ref lexer, "LIKE") || IsWord(ref lexer, "ILIKE") || IsWord(ref lexer, "SIMILAR");
            if (restricted || !predicate || minimum > 3) { break; }
            if (previous == 3) { AddSyntaxDiagnostic(ref lexer, "Predicates require parentheses to chain."); SkipQueryRemainder(ref lexer); break; }
            if (notBefore) { Advance(ref lexer); }
            if (IsWord(ref lexer, "ILIKE") || IsWord(ref lexer, "SIMILAR"))
            {
                RejectPredicateOperator(ref lexer, pos, (notBefore ? "NOT " : "") +
                    (IsWord(ref lexer, "ILIKE") ? "ILIKE" : "SIMILAR TO"));
                break;
            }
            if (IsWord(ref lexer, "BETWEEN"))
            {
                Advance(ref lexer);
                var low = ParseOperand(ref lexer, OperandRule.Restricted);
                ExpectKeyword(ref lexer, "AND", "AND between the BETWEEN bounds");
                var high = ParseOperand(ref lexer, OperandRule.PatternOperand);
                left = Nest(ref lexer, new SqlBetweenExpression(left, low, high, notBefore, Location.Create(1, 1, pos, pos)));
            }
            else if (IsWord(ref lexer, "IN"))
            {
                left = ParseInPredicate(ref lexer, left, notBefore);
                // Both list and subquery rules are complete after the closing parenthesis
                // (gram.y:15632-15668), so their result can feed another predicate.
                previous = left is SqlInExpression ? 0 : 3;
                continue;
            }
            else if (IsWord(ref lexer, "LIKE"))
            {
                Advance(ref lexer);
                var pattern = ParseOperand(ref lexer, OperandRule.PatternOperand);
                SqlExpression? escape = null;
                if (IsWord(ref lexer, "ESCAPE"))
                {
                    Advance(ref lexer);
                    escape = ParseOperand(ref lexer, OperandRule.PatternOperand);
                }
                left = Nest(ref lexer, new SqlLikeExpression(left, pattern, notBefore, Location.Create(1, 1, pos, pos), escape));
            }
            else
            {
                if (notBefore && IsWord(ref lexer, "NULL"))
                {
                    AddSyntaxDiagnostic(ref lexer, "Unexpected NULL after NOT; write IS NOT NULL.");
                    Advance(ref lexer);
                }
                else { AddExpectedDiagnostic(ref lexer, "BETWEEN, IN or LIKE after NOT"); }
                break;
            }
            previous = 3;
        }
        return left;
    }

    private void RejectPredicateOperator(ref TokenLexer lexer, int start, string form)
    {
        int end = lexer.Current.Position + lexer.Current.Value.Length;
        bool pair = form is "<<" or ">>";
        bool similar = IsWord(ref lexer, "SIMILAR");
        bool cast = lexer.Current.Type == TokenType.ColonColon;
        bool of = IsWord(ref lexer, "OF");
        bool terminal = IsWord(ref lexer, "JSON") || IsWord(ref lexer, "NORMALIZED");
        if ((pair || similar) && TryPeekToken(lexer, out string next, out int nextEnd) &&
            (pair || next.Equals("TO", StringComparison.OrdinalIgnoreCase)))
        {
            end = nextEnd;
            Advance(ref lexer);
        }
        RequireSkippedConstruct(start, end, form);
        Advance(ref lexer);
        if (terminal) { return; }
        if (of || cast)
        {
            if (cast && CanStartOperand(ref lexer)) { Advance(ref lexer); }
            if (lexer.Current.Type == TokenType.LeftParen) { SkipParenthesized(ref lexer); }
            return;
        }
        if (CanStartOperand(ref lexer)) { ParseOperand(ref lexer, OperandRule.Concat); }
        if (IsWord(ref lexer, "ESCAPE"))
        {
            Advance(ref lexer);
            if (CanStartOperand(ref lexer)) { ParseOperand(ref lexer, OperandRule.Concat); }
        }
    }

    private SqlExpression ParseInPredicate(ref TokenLexer lexer, SqlExpression left, bool notBefore)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer);
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);

            // Check if it's a subquery: IN (SELECT ...)
            if (!IsAtEnd(ref lexer) && StartsQuery(lexer))
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

    private SqlExpression ParseConcat(ref TokenLexer lexer)
    {
        var left = ParseAddition(ref lexer);
        while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Concat)
        {
            int pos = lexer.Current.Position;
            Advance(ref lexer);
            var right = ParseOperand(ref lexer, OperandRule.Addition);
            left = Nest(ref lexer, new SqlBinaryExpression(left, SqlBinaryOperator.Concat, right, Location.Create(1, 1, pos, pos)));
        }
        return left;
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
        var left = ParseCollate(ref lexer);

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
            var right = ParseOperand(ref lexer, OperandRule.Collate);
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

        return ParsePrimary(ref lexer);
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

        // Whatever the primary is, it sits one level below the nodes that enclose it; the nodes
        // inside it (a call's arguments, a CASE's branches) record themselves as they are entered.
        EnterLeaf();

        if (IsAtEnd(ref lexer))
        {
            return MissingExpression(ref lexer, pos);
        }

        // Whole VALUES items and SET values are handled by ParseDefaultableValue. A bare
        // DEFAULT is never a scalar, including when a column has that quoted name
        // (PostgreSQL src/backend/parser/parse_expr.c:310-316). Dotted field names are
        // consumed inside ParseColumnRefOrFunction, where keywords remain valid labels.
        if (IsKeyword(ref lexer, "DEFAULT"))
        {
            int end = pos + lexer.Current.Value.Length;
            AddSyntaxDiagnostic(ref lexer, "DEFAULT is not allowed in this context.");
            Advance(ref lexer);
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, pos, end));
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

        if (TryParseTypedLiteral(ref lexer, out var typedLiteral)) { return typedLiteral!; }

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

        if (lexer.Current.Type == TokenType.Unrecognized && lexer.Current.Value.SequenceEqual("?"))
        {
            // Lexical preflight owns stray '?'; an operand replaces that generic diagnostic.
            _parseDiagnostics.RemoveAll(diagnostic => diagnostic.Start == pos && diagnostic.Code == "SQL0003");
            _parseDiagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("SQL0003", Location.Create(1, 1, pos, pos + 1)));
            Advance(ref lexer);
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, pos, pos + 1));
        }
        if (lexer.Current.Type == TokenType.Colon)
        {
            int end = pos + 1;
            Advance(ref lexer);
            if (lexer.Current.Position == end && lexer.Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function)
            {
                end += lexer.Current.Value.Length;
                Advance(ref lexer);
            }
            _parseDiagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("SQL0003", Location.Create(1, 1, pos, end)));
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, pos, end));
        }

        // Parameter
        if (lexer.Current.Type == TokenType.Parameter)
        {
            var name = CurrentText(ref lexer);
            if (!ParameterBindings.IsValidReference(name))
            {
                AddSyntaxDiagnostic(ref lexer, "A parameter name must be an identifier (a letter or _, then letters, digits or _) or digits only; use $name, @name, or $1.");
            }
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
            if (!IsAtEnd(ref lexer) && StartsQuery(lexer))
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
            return ParseColumnRefOrFunction(ref lexer);
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
        bool quoted = lexer.Current.Type == TokenType.QuotedIdentifier;
        string first = CurrentIdentifierText(ref lexer);
        Advance(ref lexer);

        // Niladic datetime names are value functions, never unquoted column references.
        if (!quoted && SqlUnsupportedVocabulary.IsNiladicFunction(first))
        {
            return ParseNiladic(ref lexer, first, pos);
        }

        // Check for function call: identifier(
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            return ParseFunctionCallArgs(ref lexer, first, pos, quoted);
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
                int end = CurrentTokenEnd(ref lexer);
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
                        end = CurrentTokenEnd(ref lexer);
                        Advance(ref lexer);
                        return new SqlColumnReferenceExpression(third, second, first,
                            Location.Create(1, 1, pos, end));
                    }
                }

                // a.b — could be table.column or schema.table (interpret as table.column)
                return new SqlColumnReferenceExpression(second, first, null,
                    Location.Create(1, 1, pos, end));
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

        if (SqlUnsupportedVocabulary.IsNiladicFunction(name)) { return ParseNiladic(ref lexer, name, pos); }

        // If no parens, treat as column reference
        return new SqlColumnReferenceExpression(name, null, null,
            Location.Create(1, 1, pos, pos + name.Length));
    }

    private SqlExpression ParseFunctionCallArgs(ref TokenLexer lexer, string name, int pos, bool quoted = false)
    {
        if (!quoted || name == name.ToLowerInvariant())
        {
            if (SqlUnsupportedVocabulary.IsNiladicFunction(name)) { return ParseNiladic(ref lexer, name, pos); }
            if (name.ToUpperInvariant() is "SUBSTRING" or "TRIM" or "POSITION" or "EXTRACT")
            {
                return ParseStandardForm(ref lexer, name.ToUpperInvariant(), pos);
            }
        }
        // PostgreSQL scan.l:773-783 preserves quoted case; namespace.c:1237 looks up that spelling.
        // The vocabulary stores uppercase display names; quoted built-ins use lowercase lookup names.
        if ((!quoted || string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal)) && SqlUnsupportedVocabulary.IsFunction(name))
        {
            if (!SqlUnsupportedVocabulary.IsWindowFunction(name) || _preflightClauseStart < 0)
            {
                AddUnsupportedSurfaceDiagnostic(pos, pos + name.Length + (quoted ? 2 : 0),
                    $"The SQL function {name.ToUpperInvariant()} is not supported.");
            }
            SkipParenthesized(ref lexer);
            SkipCallExtensions(ref lexer);
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, pos, pos + name.Length));
        }
        Advance(ref lexer); // consume (

        var args = new List<SqlExpression>();

        // PostgreSQL gram.y func_application: the catalog, not syntax, decides whether
        // a name is an aggregate. ALL/DISTINCT require arguments and never accept *.
        var quantifier = SqlSetQuantifier.Default;
        if (IsKeyword(ref lexer, "DISTINCT") || IsKeyword(ref lexer, "ALL"))
        {
            quantifier = IsKeyword(ref lexer, "DISTINCT") ? SqlSetQuantifier.Distinct : SqlSetQuantifier.All;
            Advance(ref lexer);
            if (lexer.Current.Type is TokenType.Asterisk or TokenType.RightParen || IsKeyword(ref lexer, "ORDER"))
            {
                AddExpectedDiagnostic(ref lexer, "an argument after the set quantifier (not *)");
            }
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
        {
            if (lexer.Current.Type == TokenType.Asterisk)
            {
                args.Add(ParseStarArgument(ref lexer));
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

        var ordering = new List<SqlOrderByColumn>();
        if (IsKeyword(ref lexer, "ORDER"))
        {
            if (args.Count == 1 && args[0] is SqlStarExpression) { AddExpectedDiagnostic(ref lexer, "')' after *"); }
            ParseAggregateOrdering(ref lexer, ordering);
        }
        bool listagg = (!quoted || name == "listagg") && name.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase);
        if (listagg && IsKeyword(ref lexer, "ON") && TryPeekToken(lexer, out string overflow, out int overflowEnd) &&
            overflow.Equals("OVERFLOW", StringComparison.OrdinalIgnoreCase))
        {
            RequireSkippedConstruct(lexer.Current.Position, overflowEnd, "ON OVERFLOW");
            SkipQueryRemainder(ref lexer);
        }
        int callEnd = CurrentTokenEnd(ref lexer);
        Expect(ref lexer, TokenType.RightParen, $"')' after the {name} arguments");
        if (listagg)
        {
            if (IsWord(ref lexer, "WITHIN")) { Advance(ref lexer); }
            else { AddExpectedDiagnostic(ref lexer, "WITHIN GROUP after LISTAGG"); }
            ExpectKeyword(ref lexer, "GROUP", "GROUP after WITHIN");
            Expect(ref lexer, TokenType.LeftParen, "'(' after WITHIN GROUP");
            if (ordering.Count > 0) { AddExpectedDiagnostic(ref lexer, "only WITHIN GROUP ordering for LISTAGG"); }
            ParseAggregateOrdering(ref lexer, ordering);
            callEnd = CurrentTokenEnd(ref lexer);
            Expect(ref lexer, TokenType.RightParen, "')' after WITHIN GROUP ordering");
        }
        SkipCallExtensions(ref lexer);
        return new SqlFunctionCallExpression(name, args, Location.Create(1, 1, pos, callEnd), quantifier, ordering, listagg);
    }

    private void ParseAggregateOrdering(ref TokenLexer lexer, List<SqlOrderByColumn> ordering)
    {
        ExpectKeyword(ref lexer, "ORDER", "ORDER BY for the aggregate input");
        ExpectKeyword(ref lexer, "BY", "BY after ORDER");
        do
        {
            // A key is an operand of the call, just as an argument is (#1151).
            ordering.Add(ParseOrderByColumn(ref lexer, aggregateInput: true));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        } while (!IsAtEnd(ref lexer));
    }

    /// <summary>
    /// Parses the <c>*</c> of <c>COUNT(*)</c>. It is the call's operand, a level below the call
    /// like any other argument, so it enters that level the same way: a call at the deepest level
    /// the limit allows has no room for it (#1151). The star used to be added without the check,
    /// so <c>COUNT(*)</c> at that level built a tree one level deeper than the limit.
    /// </summary>
    private SqlExpression ParseStarArgument(ref TokenLexer lexer)
    {
        int position = lexer.Current.Position;
        if (!TryEnterOperand(ref lexer))
        {
            return NestingPlaceholder(position);
        }

        EnterLeaf();
        var star = new SqlStarExpression(Location.Create(1, 1, position, position + 1));
        Advance(ref lexer);
        _expressionDepth--;
        return star;
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

        SqlQueryBodyExpression? subquery = null;
        if (Expect(ref lexer, TokenType.LeftParen, "'(' after EXISTS"))
        {
            if (StartsQuery(lexer, queryExpected: true))
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
            Array.Empty<SqlSelectColumn>(), Array.Empty<SqlFromItem>(),
            null, Array.Empty<SqlExpression>(), null, Array.Empty<SqlOrderByColumn>(),
            null, null, false, null, null);

        return new SqlExistsExpression(subquery, isNegated, Location.Create(1, 1, pos, pos));
    }
}
