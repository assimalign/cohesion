using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlInsertExpression ParseInsert(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume INSERT

        // INTO
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "INTO"))
            Advance(ref lexer);

        // Table reference
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            table = ParseTableReference(ref lexer);
        }
        table ??= new SqlTableReference("?", null, null);

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

        return new SqlInsertExpression(table, columns, values, selectSource, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private List<string> ParseInsertColumnList(ref TokenLexer lexer)
    {
        var columns = new List<string>();
        Advance(ref lexer); // consume (

        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            columns.Add(CurrentText(ref lexer));
            Advance(ref lexer);

            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
                {
                    columns.Add(CurrentText(ref lexer));
                    Advance(ref lexer);
                }
            }
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.RightParen)
            Advance(ref lexer);

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

    private List<SqlExpression> ParseSingleValueRow(ref TokenLexer lexer)
    {
        var values = new List<SqlExpression>();

        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.LeftParen)
            Advance(ref lexer);

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
            Advance(ref lexer);

        return values;
    }
}
