using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlTruncateExpression ParseTruncate(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        Advance(ref lexer);
        if (IsWord(ref lexer, "TABLE")) { Advance(ref lexer); }
        var tables = new List<SqlTruncateTable>();
        do
        {
            bool only = IsWord(ref lexer, "ONLY");
            if (only) { Advance(ref lexer); }
            tables.Add(new SqlTruncateTable(ParseUnaliasedTableReference(ref lexer), only));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        } while (true);

        bool restart = IsWord(ref lexer, "RESTART");
        if (restart || IsWord(ref lexer, "CONTINUE"))
        {
            Advance(ref lexer);
            if (IsWord(ref lexer, "IDENTITY")) { Advance(ref lexer); }
            else { AddExpectedDiagnostic(ref lexer, "IDENTITY"); }
        }
        bool cascade = IsWord(ref lexer, "CASCADE");
        if (cascade || IsWord(ref lexer, "RESTRICT")) { Advance(ref lexer); }
        return new SqlTruncateExpression(tables, restart, cascade, Location.Create(1, 1, start, _lastTokenEnd));
    }
}
