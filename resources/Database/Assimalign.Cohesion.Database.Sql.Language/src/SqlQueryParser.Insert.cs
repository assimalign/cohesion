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

        // INTO
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INTO"))
        {
            Advance(ref lexer);
        }

        var table = ParseRequiredTableReference(ref lexer);

        // Optional column list: (col1, col2, ...)
        IReadOnlyList<string>? columns = null;
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
        {
            // Peek: is this a column list or VALUES?
            // Column list if next tokens are identifiers separated by commas before )
            columns = ParseInsertColumnList(ref lexer);
        }

        // VALUES or SELECT
        List<IReadOnlyList<SqlExpression>>? values = null;
        SqlSelectExpression? selectSource = null;

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "VALUES"))
        {
            Advance(ref lexer);
            values = ParseValuesList(ref lexer);
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SELECT"))
        {
            selectSource = ParseSelect(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "VALUES or SELECT");
        }

        return new SqlInsertExpression(table, columns, values, selectSource, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
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
            AddSyntaxDiagnostic(ref lexer, "Expected a value in the VALUES row; an empty row is not allowed.");
        }
        else
        {
            values.Add(ParseExpression(ref lexer));
            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                values.Add(ParseExpression(ref lexer));
            }
        }

        Expect(ref lexer, TokenType.RightParen, "')' after the VALUES row");
        return values;
    }
}
