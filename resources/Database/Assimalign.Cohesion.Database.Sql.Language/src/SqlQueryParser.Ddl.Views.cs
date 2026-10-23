using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlQueryExpression ParseCreateView(ref TokenLexer lexer, int pos)
    {
        bool replace = false;
        if (IsKeyword(ref lexer, "OR"))
        {
            Advance(ref lexer);
            if (IsKeywordOrFunction(ref lexer, "REPLACE")) { replace = true; Advance(ref lexer); }
            else { AddExpectedDiagnostic(ref lexer, "REPLACE after OR"); }
        }
        if (!IsKeyword(ref lexer, "VIEW"))
        {
            RejectIdentityConstruct(ref lexer, "CREATE " + CurrentText(ref lexer).ToUpperInvariant() + " VIEW");
            return new SqlQueryExpression(SqlQueryCommandType.Create, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        Advance(ref lexer);
        if (IsKeyword(ref lexer, "IF"))
        {
            RejectIdentityConstruct(ref lexer, "CREATE VIEW IF NOT EXISTS");
            return new SqlQueryExpression(SqlQueryCommandType.Create, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        var view = ParseUnaliasedTableReference(ref lexer);
        var columns = new List<string>();
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
            while (true)
            {
                if (!IsNameToken(ref lexer)) { AddExpectedDiagnostic(ref lexer, "a view column name"); break; }
                columns.Add(CurrentIdentifierText(ref lexer));
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.Comma) { break; }
                Advance(ref lexer);
            }
            Expect(ref lexer, TokenType.RightParen, _errorViewColumnsClosingExpected);
        }
        if (IsKeyword(ref lexer, "WITH"))
        {
            RejectIdentityConstruct(ref lexer, "CREATE VIEW WITH (options)");
            return new SqlQueryExpression(SqlQueryCommandType.Create, null, Location.Create(1, 1, pos, _lastTokenEnd));
        }
        ExpectKeyword(ref lexer, "AS", "AS and the view query");
        SqlQueryBodyExpression query;
        if (IsKeyword(ref lexer, "WITH"))
        {
            query = (SqlQueryBodyExpression)ParseWith(ref lexer);
        }
        else { query = ParseQueryExpression(ref lexer); }
        if (IsKeyword(ref lexer, "WITH")) { RejectIdentityConstruct(ref lexer, "WITH [LOCAL|CASCADED] CHECK OPTION"); }
        return new SqlCreateViewExpression(view, columns.AsReadOnly(), query, replace, Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropViewExpression ParseDropView(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer);
        bool ifExists = ParseExistenceGuard(ref lexer, notExists: false);
        var view = ParseUnaliasedTableReference(ref lexer);
        bool cascade = IsWord(ref lexer, "CASCADE");
        if (cascade || IsWord(ref lexer, "RESTRICT")) { Advance(ref lexer); }
        return new SqlDropViewExpression(view, ifExists, cascade, Location.Create(1, 1, pos, _lastTokenEnd));
    }
}
