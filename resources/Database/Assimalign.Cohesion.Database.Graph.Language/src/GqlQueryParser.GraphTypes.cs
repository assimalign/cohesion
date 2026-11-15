using System;
using System.Collections.Generic;

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
        var variables = new HashSet<string>(StringComparer.Ordinal);
        var nodeNames = new HashSet<string>(StringComparer.Ordinal);
        var edgeNames = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<(int Edge, string? Source, Lexeme SourceToken, string? Target, Lexeme TargetToken)>();
        Expect(TokenType.LeftBrace, "'{'");
        while (!Failed && Current.Type != TokenType.RightBrace)
        {
            if (Is("CONSTRAINT")) { Unsupported("GRAPH TYPE CONSTRAINT", Current); break; }
            var first = Node();
            if (Take(TokenType.Minus))
            {
                Expect(TokenType.LeftBracket, "'['");
                if (Current.Type != TokenType.Colon)
                {
                    var variable = Current;
                    string name = Name();
                    if (!Failed && !variables.Add(name)) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, variable); }
                }
                if (Failed) { break; }
                Expect(TokenType.Colon, "':'");
                var typeToken = Current;
                string type = Name();
                if (!Failed && !edgeNames.Add(type)) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, typeToken); }
                if (Failed) { break; }
                Implies(required: true);
                if (!Failed && mode == GqlGraphTypeOperation.Drop && Current.Type == TokenType.LeftBrace)
                { Unsupported("GRAPH TYPE DROP BODY", Current); }
                if (Failed) { break; }
                var properties = Properties();
                Expect(TokenType.RightBracket, "']'"); Expect(TokenType.RightArrow, "'->'");
                var last = Node();
                if (!Failed && (first.Implies || last.Implies || first.Implied.Count != 0 || first.Properties.Count != 0 || last.Implied.Count != 0 || last.Properties.Count != 0))
                { Error("CG00002", ErrorMessages.GraphTypeEndpointExpected, Current); }
                if (!Failed && mode == GqlGraphTypeOperation.Drop &&
                    (first.Label is not null || first.Alias is not null || last.Label is not null || last.Alias is not null))
                { Unsupported("GRAPH TYPE DROP BODY", first.Label is not null || first.Alias is not null ? first.NameToken : last.NameToken); }
                references.Add((edges.Count, first.Label is null ? first.Alias : null, first.NameToken, last.Label is null ? last.Alias : null, last.NameToken));
                edges.Add(new(type, first.Label ?? "", last.Label ?? "", properties));
            }
            else
            {
                if (!Failed && (first.Label is null || !first.Implies)) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, first.NameToken); }
                if (!Failed && mode == GqlGraphTypeOperation.Drop && (first.Implied.Count != 0 || first.Properties.Count != 0))
                { Unsupported("GRAPH TYPE DROP BODY", first.NameToken); }
                if (!Failed && !nodeNames.Add(first.Label ?? "")) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, first.NameToken); }
                nodes.Add(new(first.Label ?? "", first.Implied, first.Properties));
                if (!Failed && first.Alias is { } alias)
                {
                    if (!variables.Add(alias)) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, first.AliasToken); }
                    else { aliases.Add(alias, first.Label ?? ""); }
                }
            }
            if (Failed) { break; }
            if (Is("IS") || Is("REQUIRE")) { Unsupported("GRAPH TYPE CONSTRAINT", Current); break; }
            if (!Take(TokenType.Comma)) { break; }
            if (Current.Type == TokenType.RightBrace) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, Current); }
        }
        Expect(TokenType.RightBrace, "'}'");
        if (!Failed && (Is("CLOSED") || Is("OPEN"))) { Unsupported("GRAPH TYPE MODE", Current); }
        foreach (var reference in references)
        {
            if (Failed) { break; }
            var edge = edges[reference.Edge];
            string Resolve(string? alias, string label, Lexeme name)
            {
                if (alias is null)
                {
                    if (label.Length == 0 && mode != GqlGraphTypeOperation.Drop && !Failed) { Unsupported("GRAPH TYPE ENDPOINT", name); }
                    return label;
                }
                if (aliases.TryGetValue(alias, out var found)) { return found; }
                if (!Failed) { Error("CG03011", ErrorMessages.GraphTypeReferenceMissing, name); }
                return "";
            }
            edge = edge with { Source = Resolve(reference.Source, edge.Source, reference.SourceToken), Target = Resolve(reference.Target, edge.Target, reference.TargetToken) };
            edges[reference.Edge] = edge;
        }
        return new GqlQueryExpression([], Span(start, Previous))
        { SchemaCommand = new() { Kind = GqlSchemaObjectKind.GraphType, GraphType = new() { Operation = mode, Nodes = nodes.AsReadOnly(), Edges = edges.AsReadOnly() } } };

        (string? Alias, Lexeme AliasToken, string? Label, Lexeme NameToken, bool Implies, IReadOnlyList<string> Implied, IReadOnlyList<GqlGraphPropertyType> Properties) Node()
        {
            var open = Current;
            Expect(TokenType.LeftParen, "'('");
            var aliasToken = Current;
            string? alias = Current.Type is not (TokenType.Colon or TokenType.RightParen) ? Name() : null;
            bool colon = Take(TokenType.Colon);
            var nameToken = colon ? Current : alias is null ? open : aliasToken;
            string? label = colon ? Name() : null;
            bool implies = Implies(required: false);
            var labels = new List<string>();
            if (!Failed && mode == GqlGraphTypeOperation.Drop && Current.Type is TokenType.Colon or TokenType.LeftBrace)
            {
                Unsupported("GRAPH TYPE DROP BODY", Current);
                return (alias, aliasToken, label, nameToken, implies, labels, []);
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (label is not null) { names.Add(label); }
            if (Take(TokenType.Colon))
            {
                do
                {
                    var token = Current;
                    string implied = Name();
                    if (!Failed && !names.Add(implied)) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, token); }
                    labels.Add(implied);
                } while (!Failed && Take(TokenType.Ampersand));
            }
            if (Failed) { return (alias, aliasToken, label, nameToken, implies, labels, []); }
            var properties = Properties();
            Expect(TokenType.RightParen, "')'");
            return (alias, aliasToken, label, nameToken, implies, labels.AsReadOnly(), properties);
        }
        string Name()
        {
            if (Failed) { return ""; }
            var token = Current;
            string name = Identifier(allowKeyword: true);
            if (!Failed && string.IsNullOrWhiteSpace(name)) { Error("CG03011", ErrorMessages.GraphTypeNameInvalid, token); }
            return name;
        }
        bool Implies(bool required)
        {
            if (Failed) { return false; }
            if (Take("IMPLIES")) { return true; }
            if (Take(TokenType.Equals)) { Expect(TokenType.GreaterThan, "'>'"); return true; }
            if (required) { Error("CG00002", ErrorMessages.GraphTypeImpliesExpected, Current); }
            return false;
        }
        IReadOnlyList<GqlGraphPropertyType> Properties()
        {
            var properties = new List<GqlGraphPropertyType>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!Take(TokenType.LeftBrace)) { return properties.AsReadOnly(); }
            while (!Failed && Current.Type != TokenType.RightBrace)
            {
                var nameToken = Current;
                string name = Name();
                if (!Failed && !names.Add(name)) { Error("CG03011", ErrorMessages.GraphTypeDuplicate, nameToken); break; }
                if (!Take(TokenType.ColonColon)) { Take("TYPED"); }
                var type = ParseSchemaPropertyType("GRAPH PROPERTY TYPE");
                if (!Failed && Current.Type is TokenType.Bang or TokenType.Pipe) { Unsupported("GRAPH PROPERTY TYPE", Current); }
                bool required = !Failed && Take("NOT");
                if (required) { Expect("NULL"); }
                if (!Failed && (Is("IS") || Is("REQUIRE"))) { Unsupported("GRAPH TYPE CONSTRAINT", Current); }
                properties.Add(new(name, type ?? default, required));
                if (Failed || !Take(TokenType.Comma)) { break; }
                if (Current.Type == TokenType.RightBrace) { Error("CG00002", ErrorMessages.GraphTypeNodeExpected, Current); }
            }
            Expect(TokenType.RightBrace, "'}'");
            return properties.AsReadOnly();
        }
    }
}
