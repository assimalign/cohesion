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

        return new SqlInsertExpression(table, columns, values, selectSource, isDefaultValues, null,
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
