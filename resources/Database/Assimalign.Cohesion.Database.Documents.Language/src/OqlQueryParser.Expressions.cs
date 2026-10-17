using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Execution.Internal;
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
            if (minimumPrecedence <= 3 && (Is("IN") || Is("BETWEEN") || Is("LIKE") || Is("NOT")))
            {
                bool negated = Take("NOT");
                if (Take("IN"))
                {
                    var values = new List<OqlExpression>();
                    if (!Take(TokenType.LeftParen))
                    {
                        _diagnostics.Add(QueryDiagnostics.UnsupportedClause("IN array form", Profile.Language, Span(Previous, Previous)));
                        break;
                    }
                    do { values.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
                    Expect(TokenType.RightParen, "')'");
                    left = new OqlInExpression(left, values.AsReadOnly(), negated, Span(start, Previous));
                }
                else if (Take("BETWEEN"))
                {
                    var lower = ParseExpression(4);
                    Expect("AND");
                    var upper = ParseExpression(4);
                    left = new OqlBetweenExpression(left, lower, upper, negated, Span(start, Previous));
                }
                else if (Take("LIKE"))
                {
                    var pattern = ParseExpression(4);
                    OqlExpression? escape = null;
                    if (Take("ESCAPE"))
                    {
                        var escapeStart = Current;
                        escape = ParseExpression(4);
                        if (escape is OqlLiteralExpression { Value: string text } && text.EnumerateRunes().Count() > 1)
                        {
                            Error("OQL0002", "LIKE ESCAPE must be empty or one Unicode scalar.", escapeStart);
                        }
                    }
                    left = new OqlLikeExpression(left, pattern, escape, negated, Span(start, Previous));
                }
                else { Error("OQL0002", "Expected IN, BETWEEN, or LIKE after NOT.", Current); }
                continue;
            }
            if (Is("IS") && minimumPrecedence <= 3)
            {
                Advance();
                bool negate = Take("NOT");
                string predicate = Current.Text.ToUpperInvariant();
                if (Is("NULL") || Is("MISSING") || Is("VALUED")) { Advance(); }
                else { Error("OQL0002", "Expected NULL, MISSING, or VALUED.", Current); }
                left = new OqlUnaryExpression($"IS {(negate ? "NOT " : string.Empty)}{predicate}", left, Span(start, Previous));
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
        if (Current.Type == TokenType.Colon || Current.Text == "?")
        {
            Advance();
            var end = start;
            if (start.Type == TokenType.Colon && Current.Start == start.End && Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function)
            {
                end = Current;
                Advance();
            }
            _parameterFormPositions.Add(start.Start);
            _diagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("OQL0002", Span(start, end)));
            return new OqlLiteralExpression(null, Span(start, end));
        }
        if (Take("NOT"))
        {
            return new OqlUnaryExpression("NOT", ParseExpression(3), Span(start, Previous));
        }

        if (Take("CASE"))
        {
            OqlExpression? operand = Is("WHEN") ? null : ParseExpression();
            var branches = new List<KeyValuePair<OqlExpression, OqlExpression>>();
            if (!Is("WHEN")) { Error("OQL0002", "CASE requires a WHEN branch.", Current); }
            while (!Failed && Take("WHEN"))
            {
                var condition = ParseExpression();
                Expect("THEN");
                branches.Add(new(condition, ParseExpression()));
            }
            OqlExpression? otherwise = !Failed && Take("ELSE") ? ParseExpression() : null;
            Expect("END");
            return new OqlCaseExpression(operand, branches.AsReadOnly(), otherwise, Span(start, Previous));
        }

        if (Take(TokenType.Plus))
        {
            return new OqlUnaryExpression("+", ParseExpression(6), Span(start, Previous));
        }

        if (Take(TokenType.Minus))
        {
            return new OqlUnaryExpression("-", ParseExpression(6), Span(start, Previous));
        }

        if (Take(TokenType.LeftBrace))
        {
            List<KeyValuePair<string, OqlExpression>> members = [];
            if (Current.Type != TokenType.RightBrace)
            {
                do
                {
                    string name;
                    if (Current.Type == TokenType.String) { name = Unquote(Current.Text); Advance(); }
                    else { name = Identifier(allowKeyword: true); }
                    Expect(TokenType.Colon, "':'");
                    members.Add(new(name, ParseExpression()));
                } while (!Failed && Take(TokenType.Comma));
            }
            Expect(TokenType.RightBrace, "'}'");
            return new OqlObjectExpression(members.AsReadOnly(), Span(start, Previous));
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

        if (Take("MISSING"))
        {
            return new OqlMissingExpression(Span(start, start));
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
            if (!ParameterBindings.IsValidReference(start.Text))
            {
                Error("OQL0002", "A parameter name must be an identifier (a letter or _, then letters, digits or _) or digits only; use $name, @name, or $1.", start);
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
            // Keep every digit until the engine applies the storage numeric domain. Only
            // adapt OQL's optional integer digits to JSON number syntax here.
            int exponent = start.Text.IndexOfAny(['e', 'E']);
            string significand = (exponent < 0 ? start.Text : start.Text[..exponent]).TrimStart('0');
            if (significand.Length == 0 || significand[0] == '.') { significand = "0" + significand; }
            string text = significand + (exponent < 0 ? string.Empty : start.Text[exponent..]);
            try
            {
                using var json = JsonDocument.Parse(text);
                return new OqlLiteralExpression(json.RootElement.Clone(), Span(start, start));
            }
            catch (JsonException)
            {
                Error("OQL0004", "The numeric literal is invalid.", start);
                return new OqlLiteralExpression(null, Span(start, start));
            }
        }
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function)
        {
            string name = Identifier();
            if (Take(TokenType.LeftParen))
            {
                string function = name.ToUpperInvariant();
                if (!Profile.Functions.Contains(function, StringComparer.OrdinalIgnoreCase))
                {
                    _diagnostics.Add(QueryDiagnostics.UnsupportedClause($"FUNCTION {name}", Profile.Language, Span(start, start)));
                }

                List<OqlExpression> arguments = [];
                if (Current.Type != TokenType.RightParen)
                {
                    do { arguments.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
                }

                Expect(TokenType.RightParen, "')'");
                if (function == "META")
                {
                    string? member = Take(TokenType.Dot) ? Identifier(allowKeyword: true) : null;
                    return new OqlMetaExpression(arguments.AsReadOnly(), member, Span(start, Previous));
                }
                if (arguments.Count != 1 || function != "COUNT" && arguments[0] is OqlStarExpression)
                {
                    Error("OQL0006", "Aggregates require one expression; only COUNT accepts '*'.", start);
                }

                return new OqlCallExpression(function, arguments.AsReadOnly(), Span(start, Previous));
            }
            return ParsePath(start, name);
        }
        Error("OQL0002", "Expected an expression.", start);
        return new OqlLiteralExpression(null, Span(start, start));
    }

    private OqlPathExpression ParseDocumentPath()
    {
        var start = Current;
        string name = Identifier();
        return ParsePath(start, name);
    }

    private OqlPathExpression ParsePath(Lexeme start, string name)
    {
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

    private static string Unquote(string text) => text.Length >= 2 ? text[1..^1].Replace("''", "'", StringComparison.Ordinal) : string.Empty;
}
