using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlUpdateExpression ParseUpdate(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume UPDATE

        var table = ParseRequiredTableReference(ref lexer);

        // SET is required: UPDATE t; used to execute with no assignments.
        var assignments = new List<SqlAssignment>();
        if (ExpectKeyword(ref lexer, "SET", "SET after the UPDATE table"))
        {
            assignments.Add(ParseAssignment(ref lexer));

            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                assignments.Add(ParseAssignment(ref lexer));
            }
        }

        // WHERE
        SqlExpression? where = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHERE"))
        {
            Advance(ref lexer);
            where = ParseExpression(ref lexer);
        }

        return new SqlUpdateExpression(table, assignments, where, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    /// <summary>
    /// Parses <c>column = value</c>. The column and the <c>=</c> are required. A clause
    /// keyword in the column position means the column is missing, so
    /// <c>SET a = 1, WHERE id = 1</c> reports the trailing comma instead of assigning
    /// <c>(id = 1)</c> to a column named WHERE on every row (#1068).
    /// </summary>
    private SqlAssignment ParseAssignment(ref TokenLexer lexer)
    {
        int position = lexer.Current.Position;
        string columnName = string.Empty;
        if (IsNameToken(ref lexer))
        {
            columnName = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "a column name after SET or ','");
            if (lexer.Current.Type != TokenType.Equals)
            {
                // Leave the clause keyword or terminator to the UPDATE parser.
                return new SqlAssignment(columnName, new SqlLiteralExpression("NULL", SqlLiteralType.Null,
                    Location.Create(1, 1, position, position)));
            }
        }

        if (lexer.Current.Type == TokenType.Equals)
        {
            Advance(ref lexer);
        }
        else
        {
            AddExpectedDiagnostic(ref lexer, "'=' after the SET column");
            if (!CanStartOperand(ref lexer))
            {
                return new SqlAssignment(columnName, new SqlLiteralExpression("NULL", SqlLiteralType.Null,
                    Location.Create(1, 1, position, position)));
            }
        }

        var value = ParseExpression(ref lexer);

        return new SqlAssignment(columnName, value);
    }
}
