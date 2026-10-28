using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private static bool StartsNextValue(TokenLexer lexer)
        => MoveTypeLookahead(ref lexer) && IsWord(ref lexer, "VALUE") &&
            MoveTypeLookahead(ref lexer) && IsWord(ref lexer, "FOR");

    private SqlQueryExpression ParseSequence(ref TokenLexer lexer, int position, bool alter)
    {
        RequireSchemaClause(ref lexer, alter ? SqlClauses.AlterSequence : SqlClauses.CreateSequence);
        Advance(ref lexer);
        bool guard = ParseExistenceGuard(ref lexer, notExists: !alter);
        var name = ParseUnaliasedTableReference(ref lexer);
        var options = new List<SqlSequenceOption>();
        var seen = new HashSet<string>();
        while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            string option = CurrentText(ref lexer).ToUpperInvariant();
            bool no = option == "NO";
            Advance(ref lexer);
            if (no) { option = CurrentText(ref lexer).ToUpperInvariant(); Advance(ref lexer); }
            if (!seen.Add(option)) { AddExpectedDiagnostic(ref lexer, "each sequence option at most once"); }
            if (option == "AS" && !no)
            {
                string type = CurrentText(ref lexer).ToUpperInvariant();
                if (!IsNameToken(ref lexer)) { AddExpectedDiagnostic(ref lexer, "SMALLINT, INTEGER or BIGINT"); }
                Advance(ref lexer);
                options.Add(new SqlSequenceOption(option, typeName: type));
            }
            else if (option == "CYCLE") { options.Add(new SqlSequenceOption(option, no ? 0 : 1)); }
            else if (option is "MINVALUE" or "MAXVALUE")
            { options.Add(new SqlSequenceOption(option, no ? null : ParseIdentityInteger(ref lexer))); }
            else if (!no && option is "START" or "INCREMENT" or "CACHE" or "RESTART")
            {
                if (option == "RESTART" && !alter) { AddExpectedDiagnostic(ref lexer, "CREATE SEQUENCE options without RESTART"); }
                bool with = IsWord(ref lexer, "WITH");
                if (option is "START" or "RESTART" && with || option == "INCREMENT" && IsWord(ref lexer, "BY")) { Advance(ref lexer); }
                long? value = option == "RESTART" && !with ? null : ParseIdentityInteger(ref lexer);
                options.Add(new SqlSequenceOption(option, value));
            }
            else
            {
                if (option == "OWNED") { RequireSkippedConstruct(position, _lastTokenEnd, "SEQUENCE OWNED BY"); }
                else { AddExpectedDiagnostic(ref lexer, "AS, START, INCREMENT, MINVALUE, MAXVALUE, CACHE, CYCLE or RESTART"); }
                ConsumeRemaining(ref lexer);
            }
        }
        if (alter && options.Count == 0) { AddExpectedDiagnostic(ref lexer, "at least one ALTER SEQUENCE option"); }
        var location = Location.Create(1, 1, position, _lastTokenEnd);
        return alter ? new SqlAlterSequenceExpression(name, guard, options.AsReadOnly(), location)
            : new SqlCreateSequenceExpression(name, guard, options.AsReadOnly(), location);
    }

    private SqlDropSequenceExpression ParseDropSequence(ref TokenLexer lexer, int position)
    {
        RequireSchemaClause(ref lexer, SqlClauses.DropSequence);
        Advance(ref lexer);
        bool guard = ParseExistenceGuard(ref lexer, notExists: false);
        var names = new List<SqlTableReference>();
        do
        {
            names.Add(ParseUnaliasedTableReference(ref lexer));
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
            if (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon) { AddExpectedDiagnostic(ref lexer, "a sequence name after comma"); }
        } while (!IsAtEnd(ref lexer));
        bool cascade = IsWord(ref lexer, "CASCADE");
        if (cascade || IsWord(ref lexer, "RESTRICT")) { Advance(ref lexer); }
        return new SqlDropSequenceExpression(names.AsReadOnly(), guard, cascade, Location.Create(1, 1, position, _lastTokenEnd));
    }

    private static bool IsSequenceDdlHeader(TokenLexer lexer)
    {
        if (!IsWord(ref lexer, "CREATE") && !IsWord(ref lexer, "ALTER") && !IsWord(ref lexer, "DROP")) { return false; }
        MoveTypeLookahead(ref lexer);
        return IsWord(ref lexer, "SEQUENCE");
    }

    private SqlNextValueExpression ParseNextValue(ref TokenLexer lexer, int position)
    {
        RequireSchemaClause(ref lexer, SqlClauses.NextValue);
        Advance(ref lexer);
        ExpectWord(ref lexer, "VALUE");
        ExpectWord(ref lexer, "FOR");
        var name = ParseUnaliasedTableReference(ref lexer);
        return new SqlNextValueExpression(name, Location.Create(1, 1, position, _lastTokenEnd));
    }
}
