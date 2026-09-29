using System;
using System.Globalization;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlExpression ParsePredicate()
    {
        var start = Current;
        if (++_depth > 128)
        {
            Error("GQL0005", "Predicate nesting cannot exceed 128 levels.", Current);
            _depth--;
            return new GqlLiteralExpression(null, Span(start, start));
        }
        var left = ParseComparison();
        while (!Failed && Take("AND"))
        {
            left = new GqlBinaryExpression(left, "AND", ParseComparison(), Span(start, Previous));
        }
        _depth--;
        return left;
    }

    private GqlExpression ParseComparison()
    {
        var start = Current;
        if (Take(TokenType.LeftParen))
        {
            var predicate = ParsePredicate();
            Expect(TokenType.RightParen, "')'");
            return predicate;
        }
        if (++_comparisons > 128)
        {
            Error("GQL0005", "A predicate cannot exceed 128 scalar comparisons.", start);
            return new GqlLiteralExpression(null, Span(start, start));
        }
        var left = ParseOperand();
        string op = Current.Text;
        if (Current.Type is not (TokenType.Equals or TokenType.NotEquals or TokenType.LessThan or
            TokenType.LessEqual or TokenType.GreaterThan or TokenType.GreaterEqual))
        {
            Error("GQL0002", "Expected a scalar comparison operator.", Current);
            return left;
        }
        Advance();
        return new GqlBinaryExpression(left, op == "<>" ? "!=" : op, ParseOperand(), Span(start, Previous));
    }

    private GqlExpression ParseOperand()
    {
        var start = Current;
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier)
        {
            string variable = Identifier();
            Expect(TokenType.Dot, "'.'");
            string property = Identifier(allowKeyword: true);
            return new GqlPropertyExpression(variable, property, Span(start, Previous));
        }
        return ParseLiteral();
    }

    private GqlLiteralExpression ParseLiteral()
    {
        var start = Current;
        object? value = null;
        if (Take("NULL")) { }
        else if (Take("TRUE")) { value = true; }
        else if (Take("FALSE")) { value = false; }
        else if (Take(TokenType.String)) { value = start.Text[1..^1].Replace("''", "'", StringComparison.Ordinal); }
        else
        {
            bool negative = Take(TokenType.Minus);
            if (!negative) { Take(TokenType.Plus); }
            var number = Current;
            string text = negative ? "-" + number.Text : number.Text;
            if (Take(TokenType.Integer))
            {
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)) { value = integer; }
                else { Error("GQL0004", "Integer literal is outside the signed 64-bit range.", start); }
            }
            else if (Take(TokenType.Float))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating) && double.IsFinite(floating))
                {
                    value = floating;
                }
                else { Error("GQL0004", "Floating-point literal is invalid or outside the finite double range.", start); }
            }
            else if (Current.Type is TokenType.LeftBracket or TokenType.LeftBrace) { Unsupported("COLLECTION LITERAL", Current); }
            else { Error("GQL0002", "Expected a scalar literal.", start); }
        }
        return new GqlLiteralExpression(value, Span(start, Previous));
    }
}
