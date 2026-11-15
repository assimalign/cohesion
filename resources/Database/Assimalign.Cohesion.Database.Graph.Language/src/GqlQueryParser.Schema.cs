using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private bool IsSchemaStart() => Is("ALTER") && IsWordAt(_position + 1, "CURRENT") && IsWordAt(_position + 2, "GRAPH") && IsWordAt(_position + 3, "TYPE") || (Is("CREATE") || Is("DROP")) &&
        (IsWordAt(_position + 1, "INDEX") || Is("CREATE") && IsWordAt(_position + 1, "TEXT") || IsWordAt(_position + 1, "CONSTRAINT") ||
         IsWordAt(_position + 1, "LABEL") || IsWordAt(_position + 1, "RELATIONSHIP") ||
         IsWordAt(_position + 1, "UNIQUE") || IsWordAt(_position + 1, "CLOSED") ||
         IsWordAt(_position + 1, "GRAPH") && IsWordAt(_position + 2, "TYPE"));

    private GqlQueryExpression ParseSchema()
    {
        if (Is("ALTER")) { return ParseGraphType(); }
        var start = Current;
        bool drop = Take("DROP");
        if (!drop) { Expect("CREATE"); }
        bool text = !drop && Take("TEXT");
        GqlSchemaObjectKind kind;
        if (text) { Expect("INDEX"); kind = GqlSchemaObjectKind.Index; }
        else if (Take("INDEX")) { kind = GqlSchemaObjectKind.Index; }
        else if (Take("CONSTRAINT")) { kind = GqlSchemaObjectKind.Constraint; }
        else if (Take("LABEL")) { kind = GqlSchemaObjectKind.Label; }
        else if (Take("RELATIONSHIP")) { Expect("TYPE"); kind = GqlSchemaObjectKind.RelationshipType; }
        else { Unsupported(Is("UNIQUE") ? "UNIQUE INDEX" : "CLOSED GRAPH TYPE", Current); return EmptyExpression(); }
        string? name = null;
        if (drop || kind is GqlSchemaObjectKind.Label or GqlSchemaObjectKind.RelationshipType || !Is("IF") && !Is("FOR"))
        { name = Identifier(allowKeyword: true); }
        bool conditional = Take("IF");
        if (conditional) { if (!drop) { Expect("NOT"); } Expect("EXISTS"); }
        var command = new GqlSchemaCommand { Drop = drop, Kind = kind, IsText = text, Name = name, IfExists = conditional };
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
                    type = ParseSchemaPropertyType("CONSTRAINT TYPE");
                    if (!Failed && (Is("LIST") || Current.Type is TokenType.LeftBracket or TokenType.Bang or TokenType.Pipe || Is("NOT")))
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

    private GqlSchemaPropertyType? ParseSchemaPropertyType(string construct)
    {
        if (Failed) { return null; }
        var token = Current;
        GqlSchemaPropertyType? type = token.Type is TokenType.Identifier or TokenType.Keyword ? token.Text.ToUpperInvariant() switch
        {
            "BOOLEAN" or "BOOL" => GqlSchemaPropertyType.Boolean,
            "INT8" => GqlSchemaPropertyType.Int8,
            "INT16" => GqlSchemaPropertyType.Int16,
            "INT32" => GqlSchemaPropertyType.Int32,
            "INT64" or "INTEGER" or "INT" => GqlSchemaPropertyType.Int64,
            "FLOAT32" => GqlSchemaPropertyType.Float32,
            "FLOAT64" or "FLOAT" => GqlSchemaPropertyType.Float64,
            "DECIMAL" => GqlSchemaPropertyType.Decimal,
            "STRING" => GqlSchemaPropertyType.String,
            _ => null,
        } : null;
        if (type is null)
        {
            if (token.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function or TokenType.QuotedIdentifier)
            { Unsupported(construct + " " + token.Text, token); }
            else { Error("CG00002", ErrorMessages.GqlQueryParserIdentifierVoid2, token); }
        }
        else { Advance(); }
        return type;
    }
}
