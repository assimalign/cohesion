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
            if (Current.Type == TokenType.LeftBracket)
            {
                if (!HasStackToNest(Current)) { break; }
                Advance();
                var index = Current.Type == TokenType.DotDot ? null : ParseExpression(0);
                if (Take(TokenType.DotDot))
                {
                    var to = Current.Type == TokenType.RightBracket ? null : ParseExpression(0);
                    Expect(TokenType.RightBracket, "']'");
                    left = new GqlListSliceExpression(left, index, to, Span(start, Previous));
                    continue;
                }
                Expect(TokenType.RightBracket, "']'");
                left = new GqlSubscriptExpression(left, index!, Span(start, Previous));
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
            if ((Current.Type == TokenType.LeftParen || _position + 1 < _tokens.Count && _tokens[_position + 1].Type == TokenType.Equals) && HasComprehensionBar())
            { return ParsePatternComprehension(start); }
            if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
                _tokens[_position + 1].Text.Equals("IN", StringComparison.OrdinalIgnoreCase))
            {
                string variable = Identifier();
                Expect("IN");
                var list = ParseExpression(0);
                GqlExpression? predicate = null;
                GqlExpression? projection;
                if (Take("WHERE")) { (predicate, projection) = ParseComprehensionBody(); }
                else { projection = Take(TokenType.Pipe) ? ParseExpression(0) : null; }
                Expect(TokenType.RightBracket, "']'");
                return new GqlListComprehensionExpression(variable, list, predicate, projection, Span(start, Previous));
            }
            do { items.Add(ParseExpression(0)); } while (!Failed && Take(TokenType.Comma));
        }
        Expect(TokenType.RightBracket, "']'");
        return new GqlListExpression(items, Span(start, Previous));
    }

    private (GqlExpression Predicate, GqlExpression? Projection) ParseComprehensionBody()
    {
        // A top-level label disjunction can share '|' with projection. Prefer a complete
        // WHERE | projection parse, as Cypher25Parser.g4:714-716 does, then the filter-only
        // form. Parenthesized label disjunctions never enter this candidate list.
        var bars = new List<int>();
        List<(int Position, Lexeme Token)>? arrows = null;
        int depth = 0;
        for (int i = _position; i < _tokens.Count; i++)
        {
            var type = _tokens[i].Type;
            if (depth == 0 && type == TokenType.RightBracket) { break; }
            if (depth == 0 && type == TokenType.Pipe) { bars.Add(i); }
            if (_tokens[i].Text == "<-") { (arrows ??= []).Add((i, _tokens[i])); }
            if (type is TokenType.LeftBracket or TokenType.LeftParen or TokenType.LeftBrace) { depth++; }
            if (type is TokenType.RightBracket or TokenType.RightParen or TokenType.RightBrace) { depth--; }
        }
        int start = _position;
        int diagnostics = _diagnostics.Count;
        int outerBar = _collectionBar;
        try
        {
            for (int i = bars.Count - 1; i >= 0; i--)
            {
                // Try the suffix first: rejected label bars then cost a short suffix probe,
                // rather than reparsing an increasingly long label prefix for each bar.
                _position = bars[i] + 1;
                _collectionBar = outerBar;
                var projection = ParseExpression(0);
                int end = _position;
                if (!Failed && Current.Type == TokenType.RightBracket)
                {
                    _position = start;
                    _collectionBar = bars[i];
                    var predicate = ParseExpression(0);
                    if (!Failed && _position == bars[i]) { _position = end; return (predicate, projection); }
                }
                // Recognized unsupported syntax and the terminal stack guard must survive
                // speculative parsing; neither should become a generic fallback syntax error.
                if (_diagnostics.Count > diagnostics && _diagnostics[^1].Code is statementTooDeepCode or "CK00001") { return (projection, null); }
                _position = start;
                _diagnostics.RemoveRange(diagnostics, _diagnostics.Count - diagnostics);
                if (arrows is not null) { foreach (var arrow in arrows) { _tokens[arrow.Position] = arrow.Token; } }
            }
            _collectionBar = outerBar;
            return (ParseExpression(0), null);
        }
        finally { _collectionBar = outerBar; }
    }

    private bool HasComprehensionBar()
    {
        int depth = 0;
        bool relationship = false;
        for (int i = _position; i < _tokens.Count; i++)
        {
            if (depth == 0 && _tokens[i].Type == TokenType.Comma) { return false; }
            if (depth == 0 && i > _position && i + 1 < _tokens.Count && _tokens[i - 1].Type == TokenType.RightParen &&
                _tokens[i].Type is TokenType.Minus or TokenType.LeftArrow or TokenType.RightArrow &&
                _tokens[i + 1].Type is TokenType.LeftParen or TokenType.LeftBracket) { relationship = true; }
            if (_tokens[i].Type is TokenType.LeftBracket or TokenType.LeftParen or TokenType.LeftBrace) { depth++; }
            if (_tokens[i].Type == TokenType.RightBracket && depth == 0) { break; }
            if (_tokens[i].Type is TokenType.RightBracket or TokenType.RightParen or TokenType.RightBrace) { depth--; }
            if (_tokens[i].Text == "|" && depth == 0) { return relationship; }
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
                if (!entries.TryAdd(name, value)) { Error("CG00006", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParseMapVoid, name), key); }
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
        if (!Failed && !Is("WHEN")) { Error("CG00002", ErrorMessages.GqlQueryParserParseCaseVoid, Current); }
        while (!Failed && Take("WHEN"))
        {
            var operands = new List<(GqlExpression When, string Comparison)>();
            do
            {
                string comparison = "=";
                GqlExpression when;
                if (operand is not null && Take("IS"))
                {
                    comparison = Take("NOT") ? "IS NOT NULL" : "IS NULL";
                    Expect("NULL");
                    when = new GqlLiteralExpression(null);
                }
                else
                {
                    if (operand is not null && Current.Text == "<-")
                    {
                        comparison = "<";
                        var arrow = Current;
                        _tokens[_position] = arrow with { Type = TokenType.Minus, Text = "-", Start = arrow.Start + 1 };
                    }
                    else if (operand is not null && Current.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
                    { comparison = Current.Text; Advance(); }
                    when = ParseExpression(0);
                }
                operands.Add((when, comparison));
            } while (operand is not null && !Failed && Take(TokenType.Comma));
            Expect("THEN");
            var then = ParseExpression(0);
            foreach (var item in operands)
            { alternatives.Add(new GqlCaseAlternative(item.When, then) { Comparison = item.Comparison }); }
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
        { Error("CG00002", coalesce ? ErrorMessages.QueryParserParseCaseAbbreviationVoid : ErrorMessages.QueryParserParseCaseAbbreviationVoid2, Previous); }
        return coalesce ? new GqlCoalesceExpression(items, Span(start, Previous)) :
            new GqlNullIfExpression(items[0], items.Count > 1 ? items[1] : items[0], Span(start, Previous));
    }
}
