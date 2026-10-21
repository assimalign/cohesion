namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseCollate(ref TokenLexer lexer)
    {
        var expression = ParseUnary(ref lexer);
        while (!_columnDefault && !IsAtEnd(ref lexer) && IsKeyword(ref lexer, "COLLATE"))
        {
            int position = lexer.Current.Position;
            Advance(ref lexer);
            string name = ParseCollationName(ref lexer);
            expression = Nest(ref lexer, new SqlCollateExpression(expression, name,
                Location.Create(1, 1, position, _lastTokenEnd)));
        }
        return expression;
    }

    private string ParseCollationName(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer) || !IsNameToken(ref lexer))
        {
            AddSyntaxDiagnostic(ref lexer, "Expected a collation name after COLLATE.");
            return string.Empty;
        }

        string name = CurrentIdentifierText(ref lexer).ToLowerInvariant();
        Advance(ref lexer);
        return name;
    }
}
