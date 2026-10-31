using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private bool IsSchemaStart() => (Is("CREATE") || Is("DROP")) &&
        (IsWordAt(_position + 1, "INDEX") || IsWordAt(_position + 1, "CONSTRAINT") ||
         IsWordAt(_position + 1, "LABEL") || IsWordAt(_position + 1, "RELATIONSHIP") ||
         IsWordAt(_position + 1, "UNIQUE") || IsWordAt(_position + 1, "CLOSED") ||
         IsWordAt(_position + 1, "GRAPH") && IsWordAt(_position + 2, "TYPE"));

    private GqlQueryExpression ParseSchema()
    {
        var start = Current;
        bool drop = Take("DROP");
        if (!drop) { Expect("CREATE"); }
        GqlSchemaObjectKind kind;
        if (Take("INDEX")) { kind = GqlSchemaObjectKind.Index; }
        else if (Take("CONSTRAINT")) { kind = GqlSchemaObjectKind.Constraint; }
        else if (Take("LABEL")) { kind = GqlSchemaObjectKind.Label; }
        else if (Take("RELATIONSHIP")) { Expect("TYPE"); kind = GqlSchemaObjectKind.RelationshipType; }
        else { Unsupported(Is("UNIQUE") ? "UNIQUE INDEX" : "CLOSED GRAPH TYPE", Current); return EmptyExpression(); }
        string? name = null;
        if (drop || kind is GqlSchemaObjectKind.Label or GqlSchemaObjectKind.RelationshipType || !Is("IF") && !Is("FOR"))
        { name = Identifier(allowKeyword: true); }
        bool conditional = Take("IF");
        if (conditional) { if (!drop) { Expect("NOT"); } Expect("EXISTS"); }
        var command = new GqlSchemaCommand { Drop = drop, Kind = kind, Name = name, IfExists = conditional };
        if (!drop && kind is GqlSchemaObjectKind.Index or GqlSchemaObjectKind.Constraint)
        {
            Expect("FOR");
            Expect(TokenType.LeftParen, "'('");
            bool relationship = Take(TokenType.RightParen);
            if (relationship)
            {
                if (!Take(TokenType.LeftArrow)) { Expect(TokenType.Minus, "'-'"); }
                Expect(TokenType.LeftBracket, "'['");
            }
            string variable = Identifier();
            Expect(TokenType.Colon, "':'");
            string definition = Identifier(allowKeyword: true);
            if (relationship)
            {
                Expect(TokenType.RightBracket, "']'");
                if (!Take(TokenType.RightArrow)) { Expect(TokenType.Minus, "'-'"); }
                Expect(TokenType.LeftParen, "'('"); Expect(TokenType.RightParen, "')'");
            }
            else { Expect(TokenType.RightParen, "')'"); }
            if (relationship && kind == GqlSchemaObjectKind.Index)
            { Unsupported("RELATIONSHIP PROPERTY INDEX", start); return EmptyExpression(); }
            Expect(kind == GqlSchemaObjectKind.Index ? "ON" : "REQUIRE");
            bool parens = Take(TokenType.LeftParen);
            var propertyStart = Current;
            string reference = Identifier();
            Expect(TokenType.Dot, "'.'");
            string property = Identifier(allowKeyword: true);
            if (!Failed && !string.Equals(variable, reference, StringComparison.Ordinal))
            { Error("CG01022", ErrorMessages.SchemaVariableMismatch, propertyStart); }
            if (!Failed && Current.Type == TokenType.Comma)
            { Unsupported(kind == GqlSchemaObjectKind.Index ? "COMPOSITE INDEX" : "COMPOSITE CONSTRAINT", Current); return EmptyExpression(); }
            if (parens) { Expect(TokenType.RightParen, "')'"); }
            else if (kind == GqlSchemaObjectKind.Index) { Error("CG00002", ErrorMessages.SchemaIndexParentheses, Current); }
            GqlSchemaPropertyType? type = null;
            if (kind == GqlSchemaObjectKind.Constraint)
            {
                Expect("IS");
                if (Take("NOT")) { Expect("NULL"); }
                else if (Take(TokenType.ColonColon))
                {
                    var target = Current;
                    string spelling = Identifier(allowKeyword: true).ToUpperInvariant();
                    type = spelling switch
                    {
                        "INTEGER" or "INT" => GqlSchemaPropertyType.Int64,
                        "FLOAT" => GqlSchemaPropertyType.Float64,
                        "BOOL" => GqlSchemaPropertyType.Boolean,
                        _ => Enum.TryParse<GqlSchemaPropertyType>(spelling, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null,
                    };
                    if (!Failed && type is null) { Unsupported("CONSTRAINT TYPE " + spelling, target); }
                    if (!Failed && (Is("LIST") || Current.Type == TokenType.LeftBracket || Is("NOT")))
                    { Unsupported("CONSTRAINT TYPE", Current); }
                }
                else
                {
                    bool entityQualifier = Is("NODE") || Is("RELATIONSHIP") || Is("REL");
                    string form = Is("UNIQUE") || entityQualifier && IsWordAt(_position + 1, "UNIQUE") ? "UNIQUE CONSTRAINT"
                        : Is("KEY") || entityQualifier && IsWordAt(_position + 1, "KEY") ? "KEY CONSTRAINT" : "CONSTRAINT " + Current.Text;
                    Unsupported(form, Current);
                }
            }
            command = command with { Definition = definition, Relationship = relationship, Variable = variable, Property = property, Type = type };
        }
        return new GqlQueryExpression(Array.Empty<GqlStage>(), Span(start, Previous)) { SchemaCommand = command };
    }
}
