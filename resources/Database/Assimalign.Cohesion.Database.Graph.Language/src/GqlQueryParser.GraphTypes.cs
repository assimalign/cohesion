using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseGraphType()
    {
        var start = Current;
        Expect("ALTER"); Expect("CURRENT"); Expect("GRAPH"); Expect("TYPE");
        if (Is("CLOSED") || Is("OPEN") || IsWordAt(_position + 1, "CLOSED") || IsWordAt(_position + 1, "OPEN"))
        { Unsupported("GRAPH TYPE MODE", Current); return EmptyExpression(); }
        var operation = Current;
        GqlGraphTypeOperation mode;
        if (Take("SET")) { mode = GqlGraphTypeOperation.Set; }
        else if (Take("ADD")) { mode = GqlGraphTypeOperation.Add; }
        else if (Take("ALTER")) { mode = GqlGraphTypeOperation.Alter; }
        else if (Take("DROP")) { mode = GqlGraphTypeOperation.Drop; }
        else { Error("CG00002", ErrorMessages.GraphTypeOperationExpected, operation); return EmptyExpression(); }
        var nodes = new List<GqlGraphNodeType>();
        var edges = new List<GqlGraphEdgeType>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new List<(int Edge, string? Source, string? Target)>();
        Expect(TokenType.LeftBrace, "'{'");
        while (!Failed && Current.Type != TokenType.RightBrace)
        {
            if (Is("CONSTRAINT")) { Unsupported("GRAPH TYPE CONSTRAINT", Current); break; }
            var first = Node();
            if (Take(TokenType.Minus))
            {
                Expect(TokenType.LeftBracket, "'['");
                if (Current.Type != TokenType.Colon) { Identifier(allowKeyword: true); }
                Expect(TokenType.Colon, "':'");
                string type = Identifier(allowKeyword: true);
                Implies(required: true);
                var properties = Properties();
                Expect(TokenType.RightBracket, "']'"); Expect(TokenType.RightArrow, "'->'");
                var last = Node();
                if (!Failed && (first.Implied.Count != 0 || first.Properties.Count != 0 || last.Implied.Count != 0 || last.Properties.Count != 0))
                { Error("CG00002", ErrorMessages.GraphTypeEndpointExpected, Current); }
                references.Add((edges.Count, first.Label is null ? first.Alias : null, last.Label is null ? last.Alias : null));
                edges.Add(new(type, first.Label ?? "", last.Label ?? "", properties));
            }
            else
            {
                if (!Failed && (first.Label is null || !first.Implies)) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, Current); }
                nodes.Add(new(first.Label ?? "", first.Implied, first.Properties));
                if (first.Alias is { } alias && !aliases.TryAdd(alias, first.Label ?? "") && !Failed)
                { Error("CG03011", ErrorMessages.GraphTypeDuplicate, Current); }
            }
            if (Failed) { break; }
            if (Is("IS")) { Unsupported("GRAPH TYPE CONSTRAINT", Current); break; }
            if (!Take(TokenType.Comma)) { break; }
            if (Current.Type == TokenType.RightBrace) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, Current); }
        }
        Expect(TokenType.RightBrace, "'}'");
        if (!Failed && (Is("CLOSED") || Is("OPEN"))) { Unsupported("GRAPH TYPE MODE", Current); }
        foreach (var reference in references)
        {
            if (Failed) { break; }
            var edge = edges[reference.Edge];
            string Resolve(string? alias, string label)
            {
                if (alias is null) { return label; }
                if (aliases.TryGetValue(alias, out var found)) { return found; }
                Error("CG03011", ErrorMessages.GraphTypeReferenceMissing, operation);
                return "";
            }
            edge = edge with { Source = Resolve(reference.Source, edge.Source), Target = Resolve(reference.Target, edge.Target) };
            if (!Failed && mode != GqlGraphTypeOperation.Drop && (edge.Source.Length == 0 || edge.Target.Length == 0))
            { Unsupported("GRAPH TYPE ENDPOINT", operation); }
            edges[reference.Edge] = edge;
        }
        if (!Failed && (nodes.Select(node => node.Label).Distinct(StringComparer.Ordinal).Count() != nodes.Count ||
            edges.Select(edge => edge.Type).Distinct(StringComparer.Ordinal).Count() != edges.Count))
        { Error("CG03011", ErrorMessages.GraphTypeDuplicate, operation); }
        return new GqlQueryExpression([], Span(start, Previous))
        { SchemaCommand = new() { GraphType = new() { Operation = mode, Nodes = nodes.AsReadOnly(), Edges = edges.AsReadOnly() } } };

        (string? Alias, string? Label, bool Implies, IReadOnlyList<string> Implied, IReadOnlyList<GqlGraphPropertyType> Properties) Node()
        {
            Expect(TokenType.LeftParen, "'('");
            string? alias = Current.Type is not (TokenType.Colon or TokenType.RightParen) ? Identifier(allowKeyword: true) : null;
            string? label = Take(TokenType.Colon) ? Identifier(allowKeyword: true) : null;
            bool implies = Implies(required: false);
            var labels = new List<string>();
            if (Take(TokenType.Colon))
            {
                labels.Add(Identifier(allowKeyword: true));
                while (!Failed && Take(TokenType.Ampersand)) { labels.Add(Identifier(allowKeyword: true)); }
            }
            var properties = Properties();
            Expect(TokenType.RightParen, "')'");
            if (!Failed && (labels.Distinct(StringComparer.Ordinal).Count() != labels.Count || labels.Contains(label, StringComparer.Ordinal)))
            { Error("CG03011", ErrorMessages.GraphTypeDuplicate, operation); }
            return (alias, label, implies, labels.AsReadOnly(), properties);
        }
        bool Implies(bool required)
        {
            if (Take("IMPLIES")) { return true; }
            if (Take(TokenType.Equals)) { Expect(TokenType.GreaterThan, "'>'"); return true; }
            if (required) { Error("CG00002", ErrorMessages.GraphTypeImpliesExpected, Current); }
            return false;
        }
        IReadOnlyList<GqlGraphPropertyType> Properties()
        {
            var properties = new List<GqlGraphPropertyType>();
            if (!Take(TokenType.LeftBrace)) { return properties.AsReadOnly(); }
            while (!Failed && Current.Type != TokenType.RightBrace)
            {
                string name = Identifier(allowKeyword: true);
                if (!Take(TokenType.ColonColon)) { Take("TYPED"); }
                var scalar = Current;
                string spelling = Identifier(allowKeyword: true).ToUpperInvariant();
                GqlSchemaPropertyType? type = spelling switch
                {
                    "INTEGER" or "INT" => GqlSchemaPropertyType.Int64,
                    "FLOAT" => GqlSchemaPropertyType.Float64,
                    "BOOL" => GqlSchemaPropertyType.Boolean,
                    _ => Enum.TryParse<GqlSchemaPropertyType>(spelling, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null,
                };
                if (!Failed && type is null) { Unsupported("GRAPH PROPERTY TYPE " + spelling, scalar); }
                bool required = Take("NOT");
                if (required) { Expect("NULL"); }
                if (!Failed && Is("IS")) { Unsupported("GRAPH TYPE CONSTRAINT", Current); }
                properties.Add(new(name, type ?? default, required));
                if (!Take(TokenType.Comma)) { break; }
                if (Current.Type == TokenType.RightBrace) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, Current); }
            }
            Expect(TokenType.RightBrace, "'}'");
            if (!Failed && properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
            { Error("CG03011", ErrorMessages.GraphTypeDuplicate, operation); }
            return properties.AsReadOnly();
        }
    }
}
