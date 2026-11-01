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
            Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, Current);
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
                        left = new OqlInExpression(left, ParseExpression(4), negated, Span(start, Previous));
                        continue;
                    }
                    if (Is("SELECT"))
                    {
                        var parenthesis = Previous;
                        var queryStart = Current;
                        var query = ParseSelect();
                        if (!Failed && !query.IsValue)
                        { Error("CD00014", ErrorMessages.InSubqueryRequiresRaw, queryStart); }
                        if (!Failed) { Expect(TokenType.RightParen, "')'"); }
                        left = new OqlInExpression(left, new OqlSubqueryExpression(query, Span(parenthesis, Previous)), negated, Span(start, Previous));
                        continue;
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
                            Error("CD00002", ErrorMessages.OqlQueryParserParseExpressionVoid2, escapeStart);
                        }
                    }
                    left = new OqlLikeExpression(left, pattern, escape, negated, Span(start, Previous));
                }
                else { Error("CD00002", ErrorMessages.OqlQueryParserParseExpressionVoid3, Current); }
                continue;
            }
            if (Is("IS") && minimumPrecedence <= 4)
            {
                Advance();
                bool negate = Take("NOT");
                if (Take("DISTINCT"))
                {
                    Expect("FROM");
                    left = new OqlBinaryExpression(left, negate ? "IS NOT DISTINCT FROM" : "IS DISTINCT FROM", ParseExpression(5), Span(start, Previous));
                    continue;
                }
                string predicate = Current.Text.ToUpperInvariant();
                if (Is("NULL") || Is("MISSING") || Is("VALUED")) { Advance(); }
                else { Error("CD00002", ErrorMessages.OqlQueryParserParseExpressionVoid4, Current); }
                left = new OqlUnaryExpression($"IS {(negate ? "NOT " : string.Empty)}{predicate}", left, Span(start, Previous));
                continue;
            }
            string op = Current.Text.ToUpperInvariant();
            int precedence = op switch
            {
                "OR" => 1,
                "AND" => 2,
                "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=" => 3,
                "||" => 5,
                "+" or "-" => 6,
                "*" or "/" or "%" => 7,
                _ => -1,
            };
            if (precedence < minimumPrecedence)
            {
                break;
            }

            Advance();
            var right = ParseExpression(precedence + 1);
            left = op == "||"
                ? new OqlCallExpression("CONCAT", [left, right], Span(start, Previous))
                : new OqlBinaryExpression(left, op == "<>" ? "!=" : op, right, Span(start, Previous));
        }
        _depth--;
        return left;
    }

    private OqlExpression ParsePrimary()
    {
        var start = Current;
        if ((Is("ARRAY") || Is("FIRST")) && IsComprehension(_position))
        {
            bool first = Is("FIRST");
            Advance();
            var projection = ParseExpression();
            if (Failed) { return projection; }
            Expect("FOR");
            if (Failed) { return projection; }
            string variable = Identifier();
            if (Failed) { return projection; }
            if (!Failed && Current.Type == TokenType.Colon)
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("position-variable comprehension", Profile.Language, Span(Current, Current)));
                return new OqlComprehensionExpression(variable, new OqlLiteralExpression(null), projection, isFirst: first, location: Span(start, Previous));
            }
            Expect("IN");
            if (Failed) { return projection; }
            var collection = ParseExpression();
            if (Failed) { return projection; }
            if (!Failed && Current.Type == TokenType.Comma && IsQuantifierBinding(_position + 1))
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("multi-binding comprehension", Profile.Language, Span(Current, Current)));
                return new OqlComprehensionExpression(variable, collection, projection, isFirst: first, location: Span(start, Previous));
            }
            OqlExpression? predicate = !Failed && Take("WHEN") ? ParseExpression() : null;
            if (Failed) { return projection; }
            Expect("END");
            return new OqlComprehensionExpression(variable, collection, projection, predicate, first, Span(start, Previous));
        }
        if ((Is("ANY") || Is("SOME") || Is("EVERY")) &&
            (IsQuantifierBinding(_position + 1) || Is("ANY") || Is("SOME")))
        {
            bool every = Is("EVERY");
            Advance();
            string variable = Identifier();
            Expect("IN");
            var collection = ParseExpression();
            var variables = new HashSet<string>(StringComparer.Ordinal) { variable };
            Lexeme? extraBinding = null;
            while (!Failed && Current.Type == TokenType.Comma && IsQuantifierBinding(_position + 1))
            {
                extraBinding ??= Current;
                Advance();
                var nameToken = Current;
                string name = Identifier();
                if (!variables.Add(name)) { Error("CD00010", ErrorMessages.BindingNameDuplicate, nameToken); break; }
                Expect("IN");
                ParseExpression();
            }
            if (Failed) { return new OqlQuantifiedExpression(variable, collection, new OqlLiteralExpression(null), every, Span(start, Previous)); }
            if (extraBinding is { } extra)
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("multi-binding quantifier", Profile.Language, Span(extra, extra)));
                return new OqlQuantifiedExpression(variable, collection, new OqlLiteralExpression(null), every, Span(start, Previous));
            }
            Expect("SATISFIES");
            var predicate = ParseExpression();
            Expect("END");
            return new OqlQuantifiedExpression(variable, collection, predicate, every, Span(start, Previous));
        }
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
            _diagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("CD00002", Span(start, end)));
            return new OqlLiteralExpression(null, Span(start, end));
        }
        if (Take("EXISTS"))
        {
            return new OqlUnaryExpression("EXISTS", ParseExpression(4), Span(start, Previous));
        }
        if (Take("NOT"))
        {
            return new OqlUnaryExpression("NOT", ParseExpression(3), Span(start, Previous));
        }

        if (Take("CASE"))
        {
            OqlExpression? operand = Is("WHEN") ? null : ParseExpression();
            var branches = new List<KeyValuePair<OqlExpression, OqlExpression>>();
            if (!Is("WHEN")) { Error("CD00002", ErrorMessages.OqlQueryParserParsePrimaryVoid, Current); }
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
            return new OqlUnaryExpression("+", ParseExpression(8), Span(start, Previous));
        }

        if (Take(TokenType.Minus))
        {
            return new OqlUnaryExpression("-", ParseExpression(8), Span(start, Previous));
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
                    if (Failed) { break; }
                    Expect(TokenType.Colon, "':'");
                    if (Failed) { break; }
                    members.Add(new(name, ParseExpression()));
                } while (!Failed && Take(TokenType.Comma));
            }
            if (!Failed) { Expect(TokenType.RightBrace, "'}'"); }
            return new OqlObjectExpression(members.AsReadOnly(), Span(start, Previous));
        }
        if (Take(TokenType.LeftBracket))
        {
            List<OqlExpression> elements = [];
            if (Current.Type != TokenType.RightBracket)
            {
                do { elements.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
            }
            if (!Failed) { Expect(TokenType.RightBracket, "']'"); }
            return new OqlArrayExpression(elements.AsReadOnly(), Span(start, Previous));
        }
        if (Take(TokenType.LeftParen))
        {
            if (Is("SELECT"))
            {
                var query = ParseSelect();
                if (!Failed) { Expect(TokenType.RightParen, "')'"); }
                return new OqlSubqueryExpression(query, Span(start, Previous));
            }
            var expression = ParseExpression();
            if (!Failed) { Expect(TokenType.RightParen, "')'"); }
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
                Error("CD00002", ErrorMessages.OqlQueryParserParsePrimaryVoid2, start);
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
                Error("CD00004", ErrorMessages.OqlQueryParserParsePrimaryVoid3, start);
                return new OqlLiteralExpression(null, Span(start, start));
            }
        }
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function)
        {
            string name = Identifier();
            if (Take(TokenType.LeftParen))
            {
                string function = name.ToUpperInvariant();
                var quantifierToken = Current;
                var quantifier = Take("DISTINCT") ? OqlAggregateQuantifier.Distinct :
                    Take("ALL") ? OqlAggregateQuantifier.All : OqlAggregateQuantifier.None;
                List<OqlExpression> arguments = [];
                if (Current.Type != TokenType.RightParen)
                {
                    do { arguments.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
                }

                Expect(TokenType.RightParen, "')'");
                var definition = OqlFunctions.Find(function);
                if (!Failed && definition is null && !_allowUnregisteredFunctions)
                {
                    Error("CD00008", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParsePrimaryVoid4, name), start);
                }
                else if (!Failed && definition is not null && (!definition.AcceptsArity(arguments.Count) ||
                    function != "COUNT" && arguments.Any(argument => argument is OqlStarExpression)))
                {
                    Error("CD00006", function != "COUNT" && arguments.Any(argument => argument is OqlStarExpression)
                        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParsePrimaryVoid5, function)
                        : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParsePrimaryVoid6, function), start);
                }
                if (!Failed && quantifier != OqlAggregateQuantifier.None &&
                    (definition is { IsAggregate: false } || arguments.Count != 1 || arguments[0] is OqlStarExpression))
                { Error("CD00011", ErrorMessages.AggregateQuantifierInvalid, quantifierToken); }
                if (function == "META")
                {
                    string? member = Take(TokenType.Dot) ? Identifier(allowKeyword: true) : null;
                    return new OqlMetaExpression(arguments.AsReadOnly(), member, Span(start, Previous));
                }
                return new OqlCallExpression(function, arguments.AsReadOnly(), Span(start, Previous), quantifier);
            }
            var path = ParsePath(start, name);
            if (Take(TokenType.Dot))
            {
                Expect(TokenType.Asterisk, "'*'");
                return new OqlUnaryExpression(".*", path, Span(start, Previous));
            }
            return path;
        }
        Error("CD00002", ErrorMessages.OqlQueryParserParsePrimaryVoid7, start);
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
            if (Current.Type == TokenType.Dot && _position + 1 < _tokens.Count && _tokens[_position + 1].Type == TokenType.Asterisk) { break; }
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
                    Error("CD00002", ErrorMessages.OqlQueryParserParsePathVoid, token);
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
