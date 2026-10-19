using System;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlDeleteExpression ParseDelete(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume DELETE
        RejectDmlTop(ref lexer);

        // FROM (optional in some dialects, but standard)
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "FROM"))
        {
            Advance(ref lexer);
        }

        var table = ParseRequiredTableReference(ref lexer, dmlTarget: true);

        // WHERE
        SqlExpression? where = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHERE"))
        {
            Advance(ref lexer);
            where = ParseExpression(ref lexer);
        }

        var returning = ParseReturning(ref lexer, table);

        return new SqlDeleteExpression(table, where, null,
            Location.Create(1, 1, pos, _lastTokenEnd), returning);
    }
}
