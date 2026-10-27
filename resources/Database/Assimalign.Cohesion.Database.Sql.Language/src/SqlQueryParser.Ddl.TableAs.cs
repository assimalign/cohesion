using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlCreateTableAsExpression ParseCreateTableAs(ref TokenLexer lexer, SqlTableReference table, bool ifNotExists, int pos)
    {
        var names = new List<string>();
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
            while (true)
            {
                if (!IsNameToken(ref lexer)) { AddExpectedDiagnostic(ref lexer, "an output column name"); break; }
                names.Add(CurrentIdentifierText(ref lexer));
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.Comma) { break; }
                Advance(ref lexer);
            }
            Expect(ref lexer, TokenType.RightParen, "')' after the output column names");
        }
        ExpectKeyword(ref lexer, "AS", "AS and the source query");
        SqlQueryBodyExpression query;
        if (IsKeyword(ref lexer, "WITH"))
        {
            var statement = ParseWith(ref lexer);
            if (statement is SqlQueryBodyExpression body) { query = body; }
            else
            {
                AddExpectedDiagnostic(ref lexer, "a SELECT query after AS");
                query = ParseQueryExpression(ref lexer);
            }
        }
        else { query = ParseQueryExpression(ref lexer); }
        bool withData = true;
        if (IsKeyword(ref lexer, "WITH"))
        {
            Advance(ref lexer);
            if (IsWord(ref lexer, "NO")) { withData = false; Advance(ref lexer); }
            if (IsWord(ref lexer, "DATA")) { Advance(ref lexer); }
            else { AddExpectedDiagnostic(ref lexer, "DATA after WITH [NO]"); }
        }
        return new SqlCreateTableAsExpression(table, names.AsReadOnly(), query, ifNotExists, withData,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }
}
