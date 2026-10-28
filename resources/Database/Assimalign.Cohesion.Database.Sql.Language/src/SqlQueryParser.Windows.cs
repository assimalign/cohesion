using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseOver(ref TokenLexer lexer, SqlFunctionCallExpression call)
    {
        RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), SqlClauses.Over);
        int overStart = lexer.Current.Position;
        int overEnd = CurrentTokenEnd(ref lexer);
        int diagnosticsBefore = _parseDiagnostics.Count;
        Advance(ref lexer);
        string? name = null;
        SqlWindowSpecification? specification = null;
        if (lexer.Current.Type == TokenType.LeftParen) { specification = ParseWindowSpecification(ref lexer); }
        else { name = ParseWindowName(ref lexer); }
        if (!IsExecutableWindow(call.FunctionName) && diagnosticsBefore == _parseDiagnostics.Count)
        {
            RequireSkippedConstruct(overStart, overEnd,
                call.FunctionName.ToUpperInvariant() + " OVER (window function)");
        }
        return Nest(ref lexer, new SqlWindowExpression(call, name, specification,
            Location.Create(1, 1, call.Location?.Start ?? 0, _lastTokenEnd)));
    }

    private IReadOnlyList<SqlNamedWindow> ParseWindowClause(ref TokenLexer lexer)
    {
        if (!IsKeyword(ref lexer, "WINDOW")) { return []; }
        RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), SqlClauses.Window);
        Advance(ref lexer);
        var windows = new List<SqlNamedWindow>();
        do
        {
            string name = ParseWindowName(ref lexer);
            ExpectKeyword(ref lexer, "AS", "AS after window name");
            windows.Add(new SqlNamedWindow(name, ParseWindowSpecification(ref lexer)));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        } while (!IsAtEnd(ref lexer));
        return windows.AsReadOnly();
    }

    private string ParseWindowName(ref TokenLexer lexer)
    {
        if (!IsIdentifierOrKeyword(ref lexer)) { AddExpectedDiagnostic(ref lexer, "a window name"); return ""; }
        string name = CurrentIdentifierText(ref lexer);
        Advance(ref lexer);
        return name;
    }

    private SqlWindowSpecification ParseWindowSpecification(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        Expect(ref lexer, TokenType.LeftParen, "'(' before window specification");
        if (!TryEnterParenthesis(ref lexer, start)) { return new SqlWindowSpecification(null, [], []); }
        try
        {
            string? existing = null;
            var partition = new List<SqlExpression>();
            var order = new List<SqlOrderByColumn>();
            if (IsIdentifierOrKeyword(ref lexer) && !IsKeyword(ref lexer, "PARTITION") && !IsKeyword(ref lexer, "ORDER") && !IsFrameWord(ref lexer))
            { existing = ParseWindowName(ref lexer); }
            if (IsKeyword(ref lexer, "PARTITION"))
            {
                RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), SqlClauses.Partition);
                Advance(ref lexer);
                ExpectKeyword(ref lexer, "BY", "BY after PARTITION");
                do
                {
                    partition.Add(ParseOperand(ref lexer, OperandRule.Expression));
                    if (lexer.Current.Type != TokenType.Comma) { break; }
                    Advance(ref lexer);
                } while (!IsAtEnd(ref lexer));
            }
            if (IsKeyword(ref lexer, "ORDER")) { ParseAggregateOrdering(ref lexer, order); }
            if (IsFrameWord(ref lexer))
            {
                RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), CurrentText(ref lexer).ToUpperInvariant() + " window frame");
                int depth = 0;
                while (!IsAtEnd(ref lexer))
                {
                    if (lexer.Current.Type == TokenType.RightParen && depth == 0) { break; }
                    if (lexer.Current.Type == TokenType.LeftParen) { depth++; }
                    if (lexer.Current.Type == TokenType.RightParen) { depth--; }
                    Advance(ref lexer);
                }
            }
            Expect(ref lexer, TokenType.RightParen, "')' after window specification");
            return new SqlWindowSpecification(existing, partition.AsReadOnly(), order.AsReadOnly());
        }
        finally { _parenthesisDepth--; }
    }

    private static bool IsFrameWord(ref TokenLexer lexer) => IsWord(ref lexer, "ROWS") || IsWord(ref lexer, "RANGE") ||
        IsWord(ref lexer, "GROUPS") || IsWord(ref lexer, "EXCLUDE");

    private static bool IsExecutableWindow(string name) => name.ToUpperInvariant() is
        "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "PERCENT_RANK" or "CUME_DIST" or "NTILE" or "LAG" or "LEAD";
}
