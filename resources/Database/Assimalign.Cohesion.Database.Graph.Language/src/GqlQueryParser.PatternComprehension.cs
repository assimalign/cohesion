using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    // The capability scan runs before parsing. Consume an excluded prefix as one form,
    // so WALK/SHORTEST are not mistaken for scalar calls in a list value position.
    private bool TryPatternComprehensionPrefix(int index, out int end)
    {
        end = index;
        bool head = index > 0 && _tokens[index - 1].Type == TokenType.LeftBracket ||
            index > 2 && _tokens[index - 3].Type == TokenType.LeftBracket && IsVariableLexeme(_tokens[index - 2]) &&
            _tokens[index - 1].Type == TokenType.Equals;
        if (!head || !IsPathModeAt(index) && !TryPathSearchPrefix(index, out _, out _)) { return false; }
        int depth = 0;
        bool bar = false;
        for (int i = index; i < _tokens.Count; i++)
        {
            var type = _tokens[i].Type;
            if (depth == 0 && type == TokenType.RightBracket) { end = i; return bar; }
            if (depth == 0 && type == TokenType.Pipe) { bar = true; }
            if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { depth++; }
            if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace) { depth--; }
        }
        return false;
    }

    private GqlExpression ParsePatternComprehension(Lexeme start)
    {
        string? variable = null;
        if (IsVariableToken && _tokens[_position + 1].Type == TokenType.Equals)
        { variable = Identifier(); Advance(); }
        if (IsPathModeAt(_position) || Is("ANY") || Is("ALL") || Is("SHORTEST"))
        {
            Unsupported("PATTERN COMPREHENSION PATH PREFIX", Current);
            return new GqlLiteralExpression(null, Span(start, Current));
        }
        var pattern = ParsePath(allowQuantifiers: true, out _) with { Variable = variable };
        GqlExpression? predicate = null;
        GqlExpression? projection;
        if (Take("WHERE")) { (predicate, projection) = ParseComprehensionBody(); }
        else { projection = Take(TokenType.Pipe) ? ParseExpression(0) : null; }
        if (!Failed && (projection is null || pattern.Relationships.Count == 0))
        { Error("CG00014", ErrorMessages.PatternComprehensionRequired, Current); }
        Expect(TokenType.RightBracket, "']'");
        return new GqlPatternComprehensionExpression(pattern, predicate, projection!, Span(start, Previous));
    }
}
