using System;

namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlDeleteExpression ParseDelete(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume DELETE

        // FROM (optional in some dialects, but standard)
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "FROM"))
            Advance(ref lexer);

        // Table reference
        SqlTableReference? table = null;
        if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
        {
            table = ParseTableReference(ref lexer);
        }
        table ??= new SqlTableReference("?", null, null);

        // WHERE
        SqlExpression? where = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHERE"))
        {
            Advance(ref lexer);
            where = ParseExpression(ref lexer);
        }

        return new SqlDeleteExpression(table, where, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }
}
