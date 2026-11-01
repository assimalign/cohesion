using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseOver(ref TokenLexer lexer, SqlFunctionCallExpression call, SqlWindowNullTreatment nullTreatment = SqlWindowNullTreatment.Default)
    {
        RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), SqlClauses.Over);
        Advance(ref lexer);
        string? name = null;
        SqlWindowSpecification? specification = null;
        if (lexer.Current.Type == TokenType.LeftParen) { specification = ParseWindowSpecification(ref lexer); }
        else { name = ParseWindowName(ref lexer); }
        return Nest(ref lexer, new SqlWindowExpression(call, name, specification,
            Location.Create(1, 1, call.Location?.Start ?? 0, _lastTokenEnd), nullTreatment));
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
            SqlWindowFrame? frame = null;
            if (IsWord(ref lexer, "ROWS") || IsWord(ref lexer, "RANGE") || IsWord(ref lexer, "GROUPS"))
            {
                var mode = IsWord(ref lexer, "ROWS") ? SqlWindowFrameMode.Rows : IsWord(ref lexer, "GROUPS") ? SqlWindowFrameMode.Groups : SqlWindowFrameMode.Range;
                int frameStart = lexer.Current.Position;
                RequireSkippedConstruct(frameStart, CurrentTokenEnd(ref lexer), mode == SqlWindowFrameMode.Rows ? SqlClauses.WindowRows : mode == SqlWindowFrameMode.Groups ? SqlClauses.WindowGroups : SqlClauses.WindowRange);
                Advance(ref lexer);
                bool between = IsKeyword(ref lexer, "BETWEEN");
                if (between) { Advance(ref lexer); }
                var first = ParseFrameBound(ref lexer);
                if (between) { ExpectKeyword(ref lexer, "AND", "AND between frame bounds"); }
                var last = between ? ParseFrameBound(ref lexer) : new SqlWindowFrameBound(SqlWindowFrameBoundKind.CurrentRow);
                var exclusion = SqlWindowExclusion.NoOthers;
                if (IsWord(ref lexer, "EXCLUDE"))
                {
                    RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), SqlClauses.WindowExclude);
                    Advance(ref lexer);
                    if (IsWord(ref lexer, "CURRENT")) { Advance(ref lexer); ExpectWord(ref lexer, "ROW"); exclusion = SqlWindowExclusion.CurrentRow; }
                    else if (IsWord(ref lexer, "GROUP")) { Advance(ref lexer); exclusion = SqlWindowExclusion.Group; }
                    else if (IsWord(ref lexer, "TIES")) { Advance(ref lexer); exclusion = SqlWindowExclusion.Ties; }
                    else { ExpectWord(ref lexer, "NO"); ExpectWord(ref lexer, "OTHERS"); }
                }
                frame = new SqlWindowFrame(mode, first, last, exclusion);
            }
            if (IsFrameWord(ref lexer))
            {
                AddExpectedDiagnostic(ref lexer, "a complete window frame before EXCLUDE");
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
            return new SqlWindowSpecification(existing, partition.AsReadOnly(), order.AsReadOnly(), frame);
        }
        finally { _parenthesisDepth--; }
    }

    private static bool IsFrameWord(ref TokenLexer lexer) => IsWord(ref lexer, "ROWS") || IsWord(ref lexer, "RANGE") ||
        IsWord(ref lexer, "GROUPS") || IsWord(ref lexer, "EXCLUDE");

    private static bool IsWindowValueModifier(TokenLexer lexer, string functionName)
    {
        bool from = IsKeyword(ref lexer, "FROM");
        if (from && !functionName.Equals("NTH_VALUE", StringComparison.OrdinalIgnoreCase)) { return false; }
        if (!from && !IsWord(ref lexer, "IGNORE") && !IsWord(ref lexer, "RESPECT")) { return false; }
        if (!AdvancePastComments(ref lexer)) { return false; }
        if (from ? !IsWord(ref lexer, "FIRST") && !IsWord(ref lexer, "LAST") : !IsWord(ref lexer, "NULLS")) { return false; }
        return AdvancePastComments(ref lexer) && (IsKeyword(ref lexer, "OVER") || from &&
            (IsWord(ref lexer, "IGNORE") || IsWord(ref lexer, "RESPECT")));
    }

    private SqlWindowFrameBound ParseFrameBound(ref TokenLexer lexer)
    {
        if (IsWord(ref lexer, "CURRENT"))
        {
            Advance(ref lexer);
            ExpectWord(ref lexer, "ROW");
            return new SqlWindowFrameBound(SqlWindowFrameBoundKind.CurrentRow);
        }
        bool unbounded = IsWord(ref lexer, "UNBOUNDED");
        SqlExpression? offset = null;
        if (unbounded) { Advance(ref lexer); }
        else { offset = ParseOperand(ref lexer, OperandRule.Expression); }
        bool preceding = IsWord(ref lexer, "PRECEDING");
        if (preceding || IsWord(ref lexer, "FOLLOWING")) { Advance(ref lexer); }
        else { AddExpectedDiagnostic(ref lexer, "PRECEDING or FOLLOWING after frame offset"); }
        return new SqlWindowFrameBound(unbounded
            ? preceding ? SqlWindowFrameBoundKind.UnboundedPreceding : SqlWindowFrameBoundKind.UnboundedFollowing
            : preceding ? SqlWindowFrameBoundKind.Preceding : SqlWindowFrameBoundKind.Following, offset);
    }
}
