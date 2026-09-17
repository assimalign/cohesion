using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlExpression ParseExpression(int minimumPrecedence = 0)
    {
        var start = Current;
        if (++_depth > 128)
        {
            Error("OQL0005", "Expression nesting exceeds 128 levels.", Current);
            _depth--;
            return new OqlLiteralExpression(null, Span(start, start));
        }
        var left = ParsePrimary();
        while (!Failed)
        {
            if (Is("IS") && minimumPrecedence <= 3)
            {
                Advance();
                bool negate = Take("NOT");
                Expect("NULL");
                left = new OqlUnaryExpression(negate ? "IS NOT NULL" : "IS NULL", left, Span(start, Previous));
                continue;
            }
            string op = Current.Text.ToUpperInvariant();
            int precedence = op switch
            {
                "OR" => 1,
                "AND" => 2,
                "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=" => 3,
                "+" or "-" => 4,
                "*" or "/" or "%" => 5,
                _ => -1,
            };
            if (precedence < minimumPrecedence)
            {
                break;
            }

            Advance();
            var right = ParseExpression(precedence + 1);
            left = new OqlBinaryExpression(left, op == "<>" ? "!=" : op, right, Span(start, Previous));
        }
        _depth--;
        return left;
    }

    private OqlExpression ParsePrimary()
    {
        var start = Current;
        if (Take("NOT"))
        {
            return new OqlUnaryExpression("NOT", ParseExpression(3), Span(start, Previous));
        }

        if (Take(TokenType.Plus))
        {
            return new OqlUnaryExpression("+", ParseExpression(6), Span(start, Previous));
        }

        if (Take(TokenType.Minus))
        {
            return new OqlUnaryExpression("-", ParseExpression(6), Span(start, Previous));
        }

        if (Take(TokenType.LeftParen))
        {
            var expression = ParseExpression();
            Expect(TokenType.RightParen, "')'");
            return expression;
        }
        if (Take(TokenType.Asterisk))
        {
            return new OqlStarExpression(Span(start, start));
        }

        if (Take("NULL") || Take("NIL"))
        {
            return new OqlLiteralExpression(null, Span(start, start));
        }

        if (Take("TRUE"))
        {
            return new OqlLiteralExpression(true, Span(start, start));
        }

        if (Take("FALSE"))
        {
            return new OqlLiteralExpression(false, Span(start, start));
        }

        if (Take(TokenType.Parameter))
        {
            if (start.Text.Length < 2)
            {
                Error("OQL0002", "A parameter needs a name or number.", start);
            }

            return new OqlParameterExpression(start.Text[1..], Span(start, start));
        }
        if (Take(TokenType.String))
        {
            return new OqlLiteralExpression(Unquote(start.Text), Span(start, start));
        }

        if (Current.Type is TokenType.Integer or TokenType.Float)
        {
            Advance();
            if (!decimal.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                Error("OQL0004", "The numeric literal is invalid or outside the decimal range.", start);
            }

            return new OqlLiteralExpression(number, Span(start, start));
        }
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function)
        {
            string name = Identifier();
            if (Take(TokenType.LeftParen))
            {
                string function = name.ToUpperInvariant();
                if (function is not ("COUNT" or "SUM" or "AVG" or "MIN" or "MAX"))
                {
                    _diagnostics.Add(QueryDiagnostics.UnsupportedClause($"FUNCTION {name}", Profile.Language, Span(start, start)));
                }

                List<OqlExpression> arguments = [];
                if (Current.Type != TokenType.RightParen)
                {
                    do { arguments.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
                }

                Expect(TokenType.RightParen, "')'");
                if (arguments.Count != 1 || function != "COUNT" && arguments[0] is OqlStarExpression)
                {
                    Error("OQL0006", "Aggregates require one expression; only COUNT accepts '*'.", start);
                }

                return new OqlCallExpression(function, arguments.AsReadOnly(), Span(start, Previous));
            }
            List<OqlPathSegment> segments = [new(name, null)];
            while (!Failed)
            {
                if (Take(TokenType.Dot))
                {
                    segments.Add(new OqlPathSegment(Identifier(allowKeyword: true), null));
                }
                else if (Take(TokenType.LeftBracket))
                {
                    var token = Current;
                    if (Take(TokenType.String))
                    {
                        segments.Add(new OqlPathSegment(Unquote(token.Text), null));
                    }
                    else if (Take(TokenType.Integer) && int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        segments.Add(new OqlPathSegment(null, index));
                    }
                    else
                    {
                        Error("OQL0002", "Expected a nonnegative array index or quoted property name.", token);
                    }

                    Expect(TokenType.RightBracket, "']'");
                }
                else
                {
                    break;
                }
            }
            return new OqlPathExpression(segments.AsReadOnly(), Span(start, Previous));
        }
        Error("OQL0002", "Expected an expression.", start);
        return new OqlLiteralExpression(null, Span(start, start));
    }

    private static string Unquote(string text) => text.Length >= 2 ? text[1..^1].Replace("''", "'", StringComparison.Ordinal) : string.Empty;
}
