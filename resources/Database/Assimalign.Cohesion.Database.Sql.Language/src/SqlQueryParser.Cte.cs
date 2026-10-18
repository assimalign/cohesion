using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlQueryExpression ParseWith(ref TokenLexer lexer, bool insertSource = false)
    {
        var definitions = new List<SqlCommonTableExpression>();
        Advance(ref lexer); // WITH
        if (IsKeyword(ref lexer, "RECURSIVE")) { return RejectWithForm(ref lexer, "RECURSIVE"); }
        do
        {
            int start = lexer.Current.Position;
            if (!IsNameToken(ref lexer))
            {
                AddExpectedDiagnostic(ref lexer, "a common table expression name");
                return EmptyWithQuery();
            }
            string name = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
            IReadOnlyList<string> columns = lexer.Current.Type == TokenType.LeftParen
                ? ParseInsertColumnList(ref lexer).AsReadOnly() : [];
            if (!ExpectKeyword(ref lexer, "AS", "AS after the common table expression name")) { return EmptyWithQuery(); }
            if (IsWord(ref lexer, "NOT") || IsWord(ref lexer, "MATERIALIZED"))
            {
                return RejectWithForm(ref lexer, "[NOT] MATERIALIZED");
            }
            if (!Expect(ref lexer, TokenType.LeftParen, "'(' before the common table expression body")) { return EmptyWithQuery(); }
            if (IsKeyword(ref lexer, "WITH")) { return RejectWithForm(ref lexer, "WITH inside a subquery or derived table"); }
            if (IsKeyword(ref lexer, "INSERT") || IsKeyword(ref lexer, "UPDATE") || IsKeyword(ref lexer, "DELETE"))
            {
                return RejectWithForm(ref lexer, $"data-modifying CTE body ({CurrentText(ref lexer)})");
            }
            if (!IsKeyword(ref lexer, "SELECT"))
            {
                AddExpectedDiagnostic(ref lexer, "SELECT in the common table expression body");
                return EmptyWithQuery();
            }
            // A body is a query scope, but not a subquery of the main command. Ordinary
            // subqueries inside it retain their own depth and correlation checks.
            var body = ParseSelect(ref lexer);
            int end = lexer.Current.Position + lexer.Current.Value.Length;
            if (!Expect(ref lexer, TokenType.RightParen, "')' after the common table expression body")) { return EmptyWithQuery(); }
            definitions.Add(new SqlCommonTableExpression(name, columns, body, Location.Create(1, 1, start, end)));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        } while (!IsAtEnd(ref lexer));

        if (IsWord(ref lexer, "SEARCH") || IsWord(ref lexer, "CYCLE"))
        {
            return RejectWithForm(ref lexer, "SEARCH/CYCLE");
        }
        SqlQueryExpression statement;
        if (IsKeyword(ref lexer, "SELECT")) { statement = ParseSelect(ref lexer); }
        else if (!insertSource && IsKeyword(ref lexer, "INSERT")) { statement = ParseInsert(ref lexer); }
        else if (!insertSource && IsKeyword(ref lexer, "UPDATE")) { statement = ParseUpdate(ref lexer); }
        else if (!insertSource && IsKeyword(ref lexer, "DELETE")) { statement = ParseDelete(ref lexer); }
        else
        {
            AddExpectedDiagnostic(ref lexer, insertSource ? "SELECT after WITH" : "SELECT, INSERT, UPDATE or DELETE after WITH");
            return EmptyWithQuery();
        }
        var commonTables = definitions.AsReadOnly();
        switch (statement)
        {
            case SqlSelectExpression select: select.CommonTables = commonTables; break;
            case SqlInsertExpression insert: insert.CommonTables = commonTables; break;
            case SqlUpdateExpression update: update.CommonTables = commonTables; break;
            case SqlDeleteExpression delete: delete.CommonTables = commonTables; break;
        }
        return statement;
    }

    private SqlSelectExpression RejectWithForm(ref TokenLexer lexer, string construct)
    {
        RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, construct);
        ConsumeRemaining(ref lexer);
        return EmptyWithQuery();
    }

    private static SqlSelectExpression EmptyWithQuery() => new([], [], null, [], null, [], null, null, false, null, null);

    // WITH ties AS (...) names a relation. WITH TIES after a paging clause does not.
    private static bool WithStartsNamedDefinition(TokenLexer lexer)
    {
        if (!NextWithToken(ref lexer) || !NextWithToken(ref lexer)) { return false; }
        if (IsKeyword(ref lexer, "AS")) { return true; }
        if (lexer.Current.Type != TokenType.LeftParen) { return false; }
        int depth = 1;
        while (depth > 0 && NextWithToken(ref lexer))
        {
            if (lexer.Current.Type == TokenType.LeftParen) { depth++; }
            else if (lexer.Current.Type == TokenType.RightParen) { depth--; }
        }
        if (depth != 0 || !NextWithToken(ref lexer) || !IsKeyword(ref lexer, "AS") || !NextWithToken(ref lexer)) { return false; }
        if (IsWord(ref lexer, "NOT") && !NextWithToken(ref lexer)) { return false; }
        if (IsWord(ref lexer, "MATERIALIZED") && !NextWithToken(ref lexer)) { return false; }
        return lexer.Current.Type == TokenType.LeftParen;
    }

    private static bool NextWithToken(ref TokenLexer lexer)
    {
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type != TokenType.Comment) { return true; }
        }
        return false;
    }
}
