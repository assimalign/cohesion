using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlInsertExpression ParseInsert(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume INSERT
        RejectDmlTop(ref lexer);

        if (IsWord(ref lexer, "OR"))
        {
            var scan = lexer;
            Advance(ref scan);
            if (IsWord(ref scan, "IGNORE") || scan.Current.Type == TokenType.Function &&
                scan.Current.Value.Equals("REPLACE", System.StringComparison.OrdinalIgnoreCase))
            {
                RequireSkippedConstruct(lexer.Current.Position, scan.Current.Position + scan.Current.Value.Length,
                    $"INSERT OR {CurrentText(ref scan).ToUpperInvariant()}; use ON CONFLICT");
                SkipQueryRemainder(ref lexer);
                return new SqlInsertExpression(new SqlTableReference(string.Empty, null, null), null, null, null, false, null,
                    Location.Create(1, 1, pos, _lastTokenEnd));
            }
        }

        // INTO
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INTO"))
        {
            Advance(ref lexer);
        }

        var table = ParseRequiredTableReference(ref lexer, insertTarget: true);

        // Optional column list: (col1, col2, ...)
        IReadOnlyList<string>? columns = null;
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen && !StartsQuery(lexer, queryExpected: true))
        {
            // A query source may start with SELECT through nested query parentheses.
            columns = ParseInsertColumnList(ref lexer);
        }

        if (RejectDmlOutput(ref lexer))
        {
            return new SqlInsertExpression(table, columns, null, null, false, null,
                Location.Create(1, 1, pos, _lastTokenEnd));
        }

        if (IsWord(ref lexer, "OVERRIDING"))
        {
            RejectIdentityConstruct(ref lexer, "OVERRIDING ... VALUE");
            return new SqlInsertExpression(table, columns, null, null, false, null,
                Location.Create(1, 1, pos, _lastTokenEnd));
        }

        // VALUES, DEFAULT VALUES or SELECT
        List<IReadOnlyList<SqlExpression>>? values = null;
        SqlQueryBodyExpression? selectSource = null;
        bool isDefaultValues = false;

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "VALUES"))
        {
            Advance(ref lexer);
            values = ParseValuesList(ref lexer);
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "DEFAULT"))
        {
            Advance(ref lexer);
            isDefaultValues = ExpectKeyword(ref lexer, "VALUES", "VALUES after DEFAULT");
        }
        else if (IsKeyword(ref lexer, "WITH"))
        {
            selectSource = (SqlQueryBodyExpression)ParseWith(ref lexer, insertSource: true);
        }
        else if (!IsAtEnd(ref lexer) && StartsQuery(lexer, queryExpected: true))
        {
            selectSource = ParseQueryExpression(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "VALUES, DEFAULT VALUES or SELECT");
        }

        var onConflict = ParseOnConflict(ref lexer);
        var returning = ParseReturning(ref lexer, table, onConflict is not null);

        return new SqlInsertExpression(table, columns, values, selectSource, isDefaultValues, null,
            Location.Create(1, 1, pos, _lastTokenEnd), returning, onConflict);
    }

    private SqlOnConflictClause? ParseOnConflict(ref TokenLexer lexer)
    {
        if (!IsWord(ref lexer, "ON")) { return null; }
        var scan = lexer;
        Advance(ref scan);
        if (IsWord(ref scan, "DUPLICATE"))
        {
            RequireSkippedConstruct(lexer.Current.Position, scan.Current.Position + scan.Current.Value.Length,
                "ON DUPLICATE KEY UPDATE; use ON CONFLICT");
            SkipQueryRemainder(ref lexer);
            return null;
        }
        if (!IsWord(ref scan, "CONFLICT")) { return null; }
        int start = lexer.Current.Position;
        Advance(ref lexer);
        Advance(ref lexer);
        RequireSkippedConstruct(start, _lastTokenEnd, SqlClauses.OnConflict);
        var kind = SqlOnConflictTargetKind.None;
        IReadOnlyList<string> columns = [];
        string? constraint = null;
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            kind = SqlOnConflictTargetKind.Columns;
            // Inference accepts plain names only. Recognize the complete excluded form once.
            scan = lexer;
            Advance(ref scan);
            while (IsNameToken(ref scan))
            {
                Advance(ref scan);
                if (scan.Current.Type != TokenType.Comma) { break; }
                Advance(ref scan);
            }
            if (scan.Current.Type is not (TokenType.RightParen or TokenType.Comma or TokenType.Semicolon) && !IsAtEnd(ref scan))
            {
                RequireSkippedConstruct(start, scan.Current.Position + scan.Current.Value.Length,
                    "ON CONFLICT inference expressions, COLLATE, ordering or operator classes");
                SkipQueryRemainder(ref lexer);
                return null;
            }
            columns = ParseInsertColumnList(ref lexer);
        }
        else if (IsWord(ref lexer, "ON"))
        {
            kind = SqlOnConflictTargetKind.Constraint;
            Advance(ref lexer);
            ExpectKeyword(ref lexer, "CONSTRAINT", "CONSTRAINT after ON CONFLICT ON");
            if (IsNameToken(ref lexer)) { constraint = CurrentIdentifierText(ref lexer); Advance(ref lexer); }
            else { AddExpectedDiagnostic(ref lexer, "a constraint name"); }
        }
        if (IsKeyword(ref lexer, "WHERE"))
        {
            RequireSkippedConstruct(start, lexer.Current.Position + lexer.Current.Value.Length, "ON CONFLICT target WHERE (partial index inference)");
            SkipQueryRemainder(ref lexer);
            return null;
        }
        if (!IsWord(ref lexer, "DO")) { AddExpectedDiagnostic(ref lexer, "DO after ON CONFLICT target"); return null; }
        Advance(ref lexer);
        if (IsWord(ref lexer, "NOTHING"))
        {
            Advance(ref lexer);
            return new(kind, columns, constraint, SqlOnConflictAction.DoNothing, [], null);
        }
        if (IsKeyword(ref lexer, "SELECT"))
        {
            RequireSkippedConstruct(start, lexer.Current.Position + lexer.Current.Value.Length, "ON CONFLICT DO SELECT");
            SkipQueryRemainder(ref lexer);
            return null;
        }
        if (!ExpectKeyword(ref lexer, "UPDATE", "NOTHING or UPDATE after ON CONFLICT DO")) { return null; }
        var assignments = new List<SqlAssignment>();
        if (ExpectKeyword(ref lexer, "SET", "SET after ON CONFLICT DO UPDATE"))
        {
            assignments.Add(ParseAssignment(ref lexer));
            while (lexer.Current.Type == TokenType.Comma) { Advance(ref lexer); assignments.Add(ParseAssignment(ref lexer)); }
        }
        SqlExpression? where = null;
        if (IsKeyword(ref lexer, "WHERE")) { Advance(ref lexer); where = ParseExpression(ref lexer); }
        return new(kind, columns, constraint, SqlOnConflictAction.DoUpdate, assignments, where);
    }

    /// <summary>
    /// Parses <c>(column [, column]*)</c>. Every element must name a column:
    /// <c>(id, name,, age)</c> and <c>()</c> used to parse with the empty element dropped.
    /// </summary>
    private List<string> ParseInsertColumnList(ref TokenLexer lexer)
    {
        var columns = new List<string>();
        Advance(ref lexer); // consume (

        while (true)
        {
            if (!IsNameToken(ref lexer))
            {
                AddExpectedDiagnostic(ref lexer, "a column name");
                break;
            }

            columns.Add(CurrentIdentifierText(ref lexer));
            Advance(ref lexer);
            if (lexer.Current.Type != TokenType.Comma)
            {
                break;
            }
            Advance(ref lexer);
        }

        SkipToClosingParenthesis(ref lexer);
        return columns;
    }

    private List<IReadOnlyList<SqlExpression>> ParseValuesList(ref TokenLexer lexer)
    {
        var rows = new List<IReadOnlyList<SqlExpression>>();

        rows.Add(ParseSingleValueRow(ref lexer));

        while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
        {
            Advance(ref lexer);
            rows.Add(ParseSingleValueRow(ref lexer));
        }

        return rows;
    }

    /// <summary>
    /// Parses <c>(value [, value]*)</c>. The parentheses and at least one value are
    /// required; an empty row <c>()</c> is a syntax error here rather than a binder error.
    /// </summary>
    private List<SqlExpression> ParseSingleValueRow(ref TokenLexer lexer)
    {
        var values = new List<SqlExpression>();

        if (!Expect(ref lexer, TokenType.LeftParen, "'(' before the VALUES row"))
        {
            return values;
        }

        if (lexer.Current.Type == TokenType.RightParen)
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseSingleValueRowVoid);
        }
        else
        {
            values.Add(ParseDefaultableValue(ref lexer));
            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                values.Add(ParseDefaultableValue(ref lexer));
            }
        }

        Expect(ref lexer, TokenType.RightParen, "')' after the VALUES row");
        return values;
    }

    /// <summary>
    /// DEFAULT is a whole assignment value, never an operand. Scalar parsing rejects the bare
    /// keyword, while qualified column names retain their normal identifier parsing.
    /// </summary>
    private SqlExpression ParseDefaultableValue(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "DEFAULT"))
        {
            int start = lexer.Current.Position;
            int end = start + lexer.Current.Value.Length;
            EnterLeaf();
            Advance(ref lexer);
            return new SqlDefaultExpression(Location.Create(1, 1, start, end));
        }

        return ParseExpression(ref lexer);
    }
}
