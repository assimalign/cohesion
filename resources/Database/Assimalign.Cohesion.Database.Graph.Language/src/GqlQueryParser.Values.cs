using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private bool StartsTuple()
    {
        int depth = 0;
        for (int i = _position; i < _tokens.Count; i++)
        {
            if (_tokens[i].Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { depth++; }
            if (_tokens[i].Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            { if (--depth == 0) { return false; } }
            if (_tokens[i].Type == TokenType.Comma && depth == 1) { return true; }
        }
        return false;
    }

    private GqlExpression ParsePostfix(GqlExpression left, Lexeme start)
    {
        while (!Failed)
        {
            if (Take(TokenType.LeftBracket))
            {
                if (Current.Type == TokenType.DotDot) { Unsupported("LIST SLICING", Current); break; }
                var index = ParseExpression(0);
                if (Current.Type == TokenType.DotDot) { Unsupported("LIST SLICING", Current); break; }
                Expect(TokenType.RightBracket, "']'");
                left = new GqlSubscriptExpression(left, index, Span(start, Previous));
            }
            else if (Take(TokenType.Dot))
            {
                left = new GqlMemberExpression(left, Identifier(allowKeyword: true), Span(start, Previous));
            }
            else { break; }
        }
        return left;
    }

    private GqlExpression ParseList()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null); }
        Advance();
        List<GqlExpression> items = [];
        if (Current.Type != TokenType.RightBracket)
        {
            if ((Current.Type == TokenType.LeftParen || _tokens[_position + 1].Type == TokenType.Equals) && HasComprehensionBar())
            { Unsupported("PATTERN COMPREHENSION", Current); return new GqlListExpression(items); }
            if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier &&
                _tokens[_position + 1].Text.Equals("IN", StringComparison.OrdinalIgnoreCase))
            { Unsupported("LIST COMPREHENSION", Current); return new GqlListExpression(items); }
            do { items.Add(ParseExpression(0)); } while (!Failed && Take(TokenType.Comma));
        }
        Expect(TokenType.RightBracket, "']'");
        return new GqlListExpression(items, Span(start, Previous));
    }

    private bool HasComprehensionBar()
    {
        int depth = 0;
        for (int i = _position; i < _tokens.Count; i++)
        {
            if (_tokens[i].Type is TokenType.LeftBracket or TokenType.LeftParen or TokenType.LeftBrace) { depth++; }
            if (_tokens[i].Type == TokenType.RightBracket && depth == 0) { break; }
            if (_tokens[i].Type is TokenType.RightBracket or TokenType.RightParen or TokenType.RightBrace) { depth--; }
            if (_tokens[i].Text == "|" && depth == 0) { return true; }
        }
        return false;
    }

    private GqlExpression ParseMap()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null); }
        Advance();
        var entries = new Dictionary<string, GqlExpression>(StringComparer.Ordinal);
        if (Current.Type != TokenType.RightBrace)
        {
            do
            {
                var key = Current;
                string name = Identifier(allowKeyword: true);
                Expect(TokenType.Colon, "':'");
                var value = ParseExpression(0);
                if (!entries.TryAdd(name, value)) { Error("GQL0006", $"Duplicate map key '{name}'.", key); }
            } while (!Failed && Take(TokenType.Comma));
        }
        Expect(TokenType.RightBrace, "'}'");
        return new GqlMapExpression(entries, Span(start, Previous));
    }

    private GqlExpression ParseCase()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null); }
        Advance();
        GqlExpression? operand = Is("WHEN") ? null : ParseExpression(0);
        List<GqlCaseAlternative> alternatives = [];
        if (!Failed && !Is("WHEN")) { Error("GQL0002", "Expected WHEN in CASE.", Current); }
        while (!Failed && Take("WHEN"))
        {
            var when = ParseExpression(0);
            Expect("THEN");
            var then = ParseExpression(0);
            alternatives.Add(new GqlCaseAlternative(when, then));
        }
        GqlExpression? otherwise = Take("ELSE") ? ParseExpression(0) : null;
        Expect("END");
        return new GqlCaseExpression(operand, alternatives, otherwise, Span(start, Previous));
    }

    private GqlExpression ParseCaseAbbreviation()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null); }
        bool coalesce = Take("COALESCE");
        if (!coalesce) { Advance(); }
        Expect(TokenType.LeftParen, "'('");
        List<GqlExpression> items = [];
        do { items.Add(ParseExpression(0)); } while (!Failed && Take(TokenType.Comma));
        Expect(TokenType.RightParen, "')'");
        if (!Failed && (coalesce ? items.Count < 2 : items.Count != 2))
        { Error("GQL0002", coalesce ? "COALESCE requires at least two operands." : "NULLIF requires two operands.", Previous); }
        return coalesce ? new GqlCoalesceExpression(items, Span(start, Previous)) :
            new GqlNullIfExpression(items[0], items.Count > 1 ? items[1] : items[0], Span(start, Previous));
    }
}
