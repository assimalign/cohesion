using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlUpdateExpression ParseUpdate(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume UPDATE

        // Table reference
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            table = ParseTableReference(ref lexer);
        }
        table ??= new SqlTableReference("?", null, null);

        // SET
        var assignments = new List<SqlAssignment>();
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "SET"))
        {
            Advance(ref lexer);
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

    private SqlAssignment ParseAssignment(ref TokenLexer lexer)
    {
        string columnName = string.Empty;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            columnName = CurrentText(ref lexer);
            Advance(ref lexer);
        }

        // =
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Equals)
        {
            Advance(ref lexer);
        }

        var value = ParseExpression(ref lexer);

        return new SqlAssignment(columnName, value);
    }
}
