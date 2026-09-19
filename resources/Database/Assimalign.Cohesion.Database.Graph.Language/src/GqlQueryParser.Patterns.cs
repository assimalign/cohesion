using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private IReadOnlyList<GqlPathPattern> ParsePatterns()
    {
        List<GqlPathPattern> paths = [];
        do
        {
            List<GqlNodePattern> nodes = [ParseNode()];
            List<GqlRelationshipPattern> relationships = [];
            while (!Failed && Current.Type is TokenType.Minus or TokenType.LeftArrow)
            {
                if (relationships.Count == 64)
                {
                    Error("GQL0005", "A path pattern cannot exceed 64 relationships.", Current);
                    break;
                }
                relationships.Add(ParseRelationship());
                nodes.Add(ParseNode());
            }
            paths.Add(new GqlPathPattern(nodes.AsReadOnly(), relationships.AsReadOnly()));
        } while (!Failed && Take(TokenType.Comma));
        return paths.AsReadOnly();
    }

    private GqlNodePattern ParseNode()
    {
        Expect(TokenType.LeftParen, "'('");
        string? variable = Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier ? Identifier() : null;
        List<string> labels = [];
        while (!Failed && Take(TokenType.Colon)) { labels.Add(Identifier(allowKeyword: true)); }
        var properties = ParseProperties();
        Expect(TokenType.RightParen, "')'");
        return new GqlNodePattern(variable, labels.AsReadOnly(), properties);
    }

    private GqlRelationshipPattern ParseRelationship()
    {
        bool incoming = Take(TokenType.LeftArrow);
        if (!incoming) { Expect(TokenType.Minus, "'-'"); }
        Expect(TokenType.LeftBracket, "'['");
        string? variable = Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier ? Identifier() : null;
        string? type = Take(TokenType.Colon) ? Identifier(allowKeyword: true) : null;
        var properties = ParseProperties();
        Expect(TokenType.RightBracket, "']'");
        GqlPatternDirection direction;
        if (incoming)
        {
            Expect(TokenType.Minus, "'-'");
            direction = GqlPatternDirection.Incoming;
        }
        else if (Take(TokenType.RightArrow)) { direction = GqlPatternDirection.Outgoing; }
        else
        {
            Expect(TokenType.Minus, "'-' or '->'");
            direction = GqlPatternDirection.Undirected;
        }
        return new GqlRelationshipPattern(variable, type, direction, properties);
    }

    private IReadOnlyDictionary<string, object?> ParseProperties()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        if (Take(TokenType.LeftBrace))
        {
            if (Current.Type != TokenType.RightBrace)
            {
                do
                {
                    var start = Current;
                    string name = Identifier(allowKeyword: true);
                    Expect(TokenType.Colon, "':'");
                    var value = ParseLiteral();
                    if (!properties.TryAdd(name, value.Value)) { Error("GQL0006", $"Duplicate property '{name}'.", start); }
                } while (!Failed && Take(TokenType.Comma));
            }
            Expect(TokenType.RightBrace, "'}'");
        }
        return new ReadOnlyDictionary<string, object?>(properties);
    }
}

