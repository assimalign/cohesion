using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private bool StartsListPredicate() => Current.Type == TokenType.Keyword &&
        Current.Text.ToUpperInvariant() is "ALL" or "ANY" or "NONE" or "SINGLE" &&
        _tokens[_position + 1].Type == TokenType.LeftParen;

    private bool TakeSetQuantifierAll()
    {
        if (!Is("ALL")) { return false; }
        if (StartsListPredicate() && _position + 3 < _tokens.Count &&
            IsVariableLexeme(_tokens[_position + 2]) && IsWordAt(_position + 3, "IN"))
        {
            // ALL followed by a group was already a set quantifier: RETURN ALL (1+2),
            // count(ALL (x IN [1])). Only a top-level WHERE makes this a binding form.
            int depth = 0;
            for (int i = _position + 1; i < _tokens.Count; i++)
            {
                var type = _tokens[i].Type;
                if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { depth++; }
                if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
                { if (--depth == 0) { break; } }
                if (depth == 1 && IsWordAt(i, "WHERE") &&
                    _tokens[i - 1].Type is not (TokenType.Dot or TokenType.Colon or TokenType.Pipe or TokenType.Ampersand or TokenType.Bang) &&
                    !IsWordAt(i - 1, "LABELED")) { return false; }
            }
        }
        return Take("ALL");
    }

    private GqlExpression ParseListPredicate()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        var kind = Enum.Parse<GqlListPredicateKind>(start.Text, ignoreCase: true);
        Advance();
        Expect(TokenType.LeftParen, "'('");
        string variable = Failed ? string.Empty : Identifier();
        Expect("IN");
        var list = ParseExpression(0);
        Expect("WHERE");
        var predicate = ParseExpression(0);
        Expect(TokenType.RightParen, "')'");
        return new GqlListPredicateExpression(kind, variable, list, predicate, Span(start, Previous));
    }

    private GqlExpression ParseReduce()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        Advance();
        Expect(TokenType.LeftParen, "'('");
        string accumulator = Identifier();
        Expect(TokenType.Equals, "'='");
        var initial = ParseExpression(0);
        Expect(TokenType.Comma, "','");
        string variable = Failed ? string.Empty : Identifier();
        Expect("IN");
        var list = ParseExpression(0);
        Expect(TokenType.Pipe, "'|'");
        var body = ParseExpression(0);
        Expect(TokenType.RightParen, "')'");
        return new GqlReduceExpression(accumulator, initial, variable, list, body, Span(start, Previous));
    }
}
