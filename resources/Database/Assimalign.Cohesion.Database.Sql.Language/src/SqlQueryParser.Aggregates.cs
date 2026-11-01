using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

public sealed partial class SqlQueryParser
{
    /// <summary>Reads grouping elements recursively, retaining composite units in ROLLUP and CUBE.</summary>
    private SqlExpression ParseGroupingExpression(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        SqlGroupingSetKind? kind = null;
        if ((IsWord(ref lexer, "GROUPING") || IsKeywordOrFunction(ref lexer, "GROUPING")) && TryPeekToken(lexer, out string sets, out _) &&
            sets.Equals("SETS", StringComparison.OrdinalIgnoreCase) && HasGroupingSetsArguments(lexer))
        {
            kind = SqlGroupingSetKind.Sets;
            Advance(ref lexer);
            Advance(ref lexer);
        }
        else if ((IsWord(ref lexer, "ROLLUP") || IsWord(ref lexer, "CUBE")) &&
            TryPeekToken(lexer, out string next, out _) && next == "(")
        {
            kind = IsWord(ref lexer, "ROLLUP") ? SqlGroupingSetKind.Rollup : SqlGroupingSetKind.Cube;
            Advance(ref lexer);
        }
        else if (lexer.Current.Type == TokenType.LeftParen && IsGroupingList(lexer))
        { kind = SqlGroupingSetKind.Composite; }

        if (kind is { } operation)
        {
            Advance(ref lexer); // opening parenthesis
            if (!TryEnterParenthesis(ref lexer, start)) { return NestingPlaceholder(start); }
            var items = new List<SqlExpression>();
            try
            {
                if (lexer.Current.Type != TokenType.RightParen)
                {
                    while (!IsAtEnd(ref lexer) && !NestingExceeded)
                    {
                        items.Add(ParseGroupingExpression(ref lexer));
                        if (lexer.Current.Type != TokenType.Comma) { break; }
                        Advance(ref lexer);
                    }
                }
                else if (operation != SqlGroupingSetKind.Composite)
                { AddExpectedDiagnostic(ref lexer, "a grouping element"); }
                int end = CurrentTokenEnd(ref lexer);
                Expect(ref lexer, TokenType.RightParen, "')' after grouping elements");
                return Nest(ref lexer, new SqlGroupingSetExpression(operation, items.AsReadOnly(), Location.Create(1, 1, start, end)));
            }
            finally { _parenthesisDepth--; }
        }

        var expression = ParseExpression(ref lexer);
        var numeric = expression switch
        {
            SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float } literal => literal,
            SqlUnaryExpression
            {
                Operator: SqlUnaryOperator.Negate,
                Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float } literal,
            } => literal,
            _ => null,
        };
        if (numeric is not null)
        {
            AddUnsupportedSurfaceDiagnostic(expression.Location?.Start ?? 0, numeric.Location?.End ?? 0,
                ErrorMessages.ParserParseGroupingExpressionVoid2);
        }
        return expression;
    }

    // A parenthesized scalar still passes through the ordinary precedence parser, so
    // (a + b) * c is one key. Only empty or comma-separated lists are composite units.
    private static bool IsGroupingList(TokenLexer lexer)
    {
        int depth = 0, prefixDepth = 0;
        bool prefix = true;
        while (AdvancePastComments(ref lexer))
        {
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                depth++;
                if (prefix) { prefixDepth++; }
            }
            else if (lexer.Current.Type == TokenType.RightParen)
            {
                if (prefix) { return true; } // empty, possibly wrapped
                if (depth == 0) { return false; }
                depth--;
            }
            else if (lexer.Current.Type == TokenType.Comma && depth <= prefixDepth) { return true; }
            else
            {
                if (prefix && (IsKeyword(ref lexer, "SELECT") || IsKeyword(ref lexer, "WITH")))
                {
                    if (depth == 0) { return false; } // scalar subquery, including its FROM commas
                    prefixDepth = 0; // only a comma outside wrapped query parentheses is a grouping list
                }
                if (prefix && (IsWord(ref lexer, "ROLLUP") || IsWord(ref lexer, "CUBE") || IsKeywordOrFunction(ref lexer, "GROUPING")) &&
                    TryPeekToken(lexer, out string next, out _) && (next == "(" || next.Equals("SETS", StringComparison.OrdinalIgnoreCase))) { return true; }
                prefix = false;
            }
        }
        return false;
    }

    /// <summary>
    /// Recognizes aggregate extensions without reserving their names as column identifiers.
    /// </summary>
    private static bool TryGetUnsupportedAggregateClause(TokenLexer lexer, out string clause, out int end)
    {
        clause = string.Empty;
        end = lexer.Current.Position + lexer.Current.Value.Length;
        if (lexer.Current.Type is not (TokenType.Identifier or TokenType.Keyword or TokenType.Function) ||
            !TryPeekToken(lexer, out string next, out int nextEnd))
        {
            return false;
        }

        string token = CurrentText(ref lexer).ToUpperInvariant();
        if (token == "WITHIN" && next.Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            clause = "WITHIN GROUP";
            end = nextEnd;
        }
        return clause.Length > 0;
    }

    /// <summary>Distinguishes GROUPING SETS (...) from a column and its implicit alias.</summary>
    private static bool HasGroupingSetsArguments(TokenLexer lexer) =>
        AdvancePastComments(ref lexer) && AdvancePastComments(ref lexer) &&
        lexer.Current.Type == TokenType.LeftParen;

    /// <summary>
    /// Recognizes calls to window functions even when their required OVER clause is absent.
    /// </summary>
    private static bool IsWindowFunctionCall(TokenLexer lexer) =>
        lexer.Current.Type is TokenType.Function or TokenType.Identifier &&
        SqlUnsupportedVocabulary.IsWindowFunction(CurrentText(ref lexer)) &&
        TryPeekToken(lexer, out string next, out _) && next == "(";
}
