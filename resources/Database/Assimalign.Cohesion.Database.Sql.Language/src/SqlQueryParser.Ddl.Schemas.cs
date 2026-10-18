using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlCreateSchemaExpression ParseCreateSchema(ref TokenLexer lexer, int pos)
    {
        RequireSchemaClause(ref lexer, SqlClauses.CreateSchema);
        Advance(ref lexer);
        bool ifNotExists = ParseExistenceGuard(ref lexer, notExists: true);
        // AUTHORIZATION without an explicit name is also a recognized unsupported form.
        string name = IsWord(ref lexer, "AUTHORIZATION") ? "?" : ParseDefinitionName(ref lexer, "a schema name");
        if (IsWord(ref lexer, "AUTHORIZATION"))
        {
            RejectSchemaElement(ref lexer, "CREATE SCHEMA AUTHORIZATION");
        }
        else if (IsWord(ref lexer, "CREATE") || IsWord(ref lexer, "GRANT"))
        {
            RejectSchemaElement(ref lexer, "CREATE SCHEMA elements");
        }
        return new SqlCreateSchemaExpression(name, ifNotExists, Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropSchemaExpression ParseDropSchema(ref TokenLexer lexer, int pos)
    {
        RequireSchemaClause(ref lexer, SqlClauses.DropSchema);
        Advance(ref lexer);
        bool ifExists = ParseExistenceGuard(ref lexer, notExists: false);
        string name = ParseDefinitionName(ref lexer, "a schema name");
        bool cascade = IsWord(ref lexer, "CASCADE");
        if (cascade || IsWord(ref lexer, "RESTRICT")) { Advance(ref lexer); }
        return new SqlDropSchemaExpression(name, ifExists, cascade, Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private void RequireSchemaClause(ref TokenLexer lexer, string clause)
    {
        if (!Supports(clause))
        {
            _parseDiagnostics.Add(QueryDiagnostics.UnsupportedClause(clause, Profile.Language,
                Location.Create(1, 1, lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length)));
        }
    }

    private void RejectSchemaElement(ref TokenLexer lexer, string construct)
    {
        _parseDiagnostics.Add(QueryDiagnostics.UnsupportedClause(construct, Profile.Language,
            Location.Create(1, 1, lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length)));
        ConsumeRemaining(ref lexer);
    }
}
