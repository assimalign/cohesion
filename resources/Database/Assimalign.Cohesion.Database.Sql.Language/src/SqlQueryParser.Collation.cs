namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseCollate(ref TokenLexer lexer)
    {
        var expression = ParsePrimary(ref lexer);
        while (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLLATE"))
        {
            int position = lexer.Current.Position;
            Advance(ref lexer);
            string name = ParseCollationName(ref lexer);
            expression = new SqlCollateExpression(expression, name,
                Location.Create(1, 1, position, _lastTokenEnd));
        }
        return expression;
    }

    private string ParseCollationName(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer) || !IsIdentifierOrKeyword(ref lexer))
        {
            AddSyntaxDiagnostic(ref lexer, "Expected a collation name after COLLATE.");
            return string.Empty;
        }

        int position = lexer.Current.Position;
        string name = CurrentText(ref lexer).ToLowerInvariant();
        Advance(ref lexer);
        if (name is not ("binary" or "case_insensitive" or "case_accent_insensitive" or "invariant"))
        {
            AddUnsupportedSurfaceDiagnostic(position, position + name.Length,
                $"Collation '{name}' is not supported. Use binary, case_insensitive, case_accent_insensitive, or invariant; culture-aware and user-defined collations are deferred to #1026.");
        }
        return name;
    }
}
