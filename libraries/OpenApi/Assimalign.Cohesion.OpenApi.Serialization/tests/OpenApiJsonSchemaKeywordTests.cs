using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

public class OpenApiJsonSchemaKeywordTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: multi-type array emitted in 3.1, first type only in 3.0")]
    public void Emit_MultiType_ArrayInThreeOne_SingleInThreeZero()
    {
        var schema = new OpenApiSchema();
        schema.Types.Add(SchemaType.String);
        schema.Types.Add(SchemaType.Integer);
        var document = WithSchema(schema);

        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldContain("\"type\":[\"string\",\"integer\"]", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_0, indented: false).ShouldContain("\"type\":\"string\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: multi-type array round-trips all entries")]
    public void Parse_MultiType_KeepsAllEntries()
    {
        var json = """{"openapi":"3.1.2","info":{"title":"t","version":"1"},"components":{"schemas":{"S":{"type":["string","integer","null"]}}}}""";

        var document = OpenApiJson.Parse(json);
        var schema = document.Components!.Schemas["S"];

        schema.Types.ShouldBe([SchemaType.String, SchemaType.Integer]);
        schema.Nullable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: boolean form emitted in 3.1, structural equivalent in 3.0")]
    public void Emit_BooleanSchema_LiteralInThreeOne_EquivalentInThreeZero()
    {
        var schema = new OpenApiSchema { Type = SchemaType.Object };
        schema.Properties["anything"] = new OpenApiSchema { BooleanValue = true };
        schema.Properties["nothing"] = new OpenApiSchema { BooleanValue = false };
        var document = WithSchema(schema);

        var json = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        json.ShouldContain("\"anything\":true", Case.Sensitive);
        json.ShouldContain("\"nothing\":false", Case.Sensitive);

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);
        downLevel.ShouldContain("\"anything\":{}", Case.Sensitive);
        downLevel.ShouldContain("\"nothing\":{\"not\":{}}", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: boolean form round-trips")]
    public void Parse_BooleanSchema_RoundTrips()
    {
        var json = """{"openapi":"3.1.2","info":{"title":"t","version":"1"},"components":{"schemas":{"S":{"type":"object","additionalProperties":false,"properties":{"free":true}}}}}""";

        var document = OpenApiJson.Parse(json);
        var schema = document.Components!.Schemas["S"];

        schema.Properties["free"].BooleanValue.ShouldBe(true);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldContain("\"free\":true", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: $ref siblings kept in 3.1, dropped in 3.0")]
    public void Emit_ReferenceSiblings_KeptInThreeOne_DroppedInThreeZero()
    {
        var schema = new OpenApiSchema
        {
            Reference = new OpenApiReference { Ref = "#/components/schemas/Base" },
            Description = "Narrowed."
        };
        var document = WithSchema(schema);

        var json = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        json.ShouldContain("\"$ref\":\"#/components/schemas/Base\"", Case.Sensitive);
        json.ShouldContain("\"description\":\"Narrowed.\"", Case.Sensitive);

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);
        downLevel.ShouldContain("\"$ref\":\"#/components/schemas/Base\"", Case.Sensitive);
        downLevel.ShouldNotContain("Narrowed.", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: 2020-12 keywords emitted in 3.1, omitted in 3.0")]
    public void Emit_ExtendedVocabulary_GatedToThreeOne()
    {
        var schema = new OpenApiSchema { Type = SchemaType.Object, Comment = "internal note" };
        schema.Defs["Name"] = new OpenApiSchema { Type = SchemaType.String };
        schema.PatternProperties["^x-"] = new OpenApiSchema { Type = SchemaType.String };
        schema.PropertyNames = new OpenApiSchema { Pattern = "^[a-z]+$" };
        schema.DependentRequired["credit_card"] = ["billing_address"];
        schema.DependentSchemas["credit_card"] = new OpenApiSchema { Type = SchemaType.Object };
        schema.PrefixItems.Add(new OpenApiSchema { Type = SchemaType.Number });
        schema.Contains = new OpenApiSchema { Type = SchemaType.String };
        schema.MinContains = 1;
        schema.MaxContains = 3;
        schema.UnevaluatedItems = new OpenApiSchema { BooleanValue = false };
        schema.UnevaluatedProperties = new OpenApiSchema { BooleanValue = false };
        schema.If = new OpenApiSchema { Type = SchemaType.Object };
        schema.Then = new OpenApiSchema { Type = SchemaType.Object };
        schema.Else = new OpenApiSchema { Type = SchemaType.Object };
        schema.ContentEncoding = "base64";
        schema.ContentMediaType = "image/png";
        schema.ContentSchema = new OpenApiSchema { Type = SchemaType.String };
        var document = WithSchema(schema);

        var json = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        foreach (var keyword in new[]
        {
            "$defs", "$comment", "patternProperties", "propertyNames", "dependentRequired", "dependentSchemas",
            "prefixItems", "contains", "minContains", "maxContains", "unevaluatedItems", "unevaluatedProperties",
            "\"if\"", "\"then\"", "\"else\"", "contentEncoding", "contentMediaType", "contentSchema"
        })
        {
            json.ShouldContain(keyword, Case.Sensitive);
        }

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);
        foreach (var keyword in new[]
        {
            "$defs", "$comment", "patternProperties", "propertyNames", "dependentRequired", "dependentSchemas",
            "prefixItems", "contains", "unevaluatedItems", "unevaluatedProperties", "contentEncoding"
        })
        {
            downLevel.ShouldNotContain(keyword, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: 2020-12 keywords round-trip")]
    public void Parse_ExtendedVocabulary_RoundTrips()
    {
        var json = """{"openapi":"3.1.2","info":{"title":"t","version":"1"},"components":{"schemas":{"S":{"$defs":{"Name":{"type":"string"}},"type":"object","patternProperties":{"^x-":{"type":"string"}},"dependentRequired":{"credit_card":["billing_address"]},"prefixItems":[{"type":"number"}],"contains":{"type":"string"},"minContains":1,"if":{"type":"object"},"then":{"type":"object"},"contentEncoding":"base64"}}}}""";

        var document = OpenApiJson.Parse(json);
        var schema = document.Components!.Schemas["S"];

        schema.Defs["Name"].Type.ShouldBe(SchemaType.String);
        schema.PatternProperties["^x-"].Type.ShouldBe(SchemaType.String);
        schema.DependentRequired["credit_card"].ShouldBe(["billing_address"]);
        schema.PrefixItems.Count.ShouldBe(1);
        schema.Contains!.Type.ShouldBe(SchemaType.String);
        schema.MinContains.ShouldBe(1);
        schema.If!.Type.ShouldBe(SchemaType.Object);
        schema.Then!.Type.ShouldBe(SchemaType.Object);
        schema.ContentEncoding.ShouldBe("base64");

        var emitted = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        OpenApiJson.Parse(emitted).ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldBe(emitted);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Schema: schema identity keywords round-trip")]
    public void Parse_IdentityKeywords_RoundTrip()
    {
        var json = """{"openapi":"3.1.2","info":{"title":"t","version":"1"},"components":{"schemas":{"S":{"$id":"https://example.com/schemas/pet","$schema":"https://json-schema.org/draft/2020-12/schema","$anchor":"pet","$dynamicAnchor":"node","type":"object"}}}}""";

        var document = OpenApiJson.Parse(json);
        var schema = document.Components!.Schemas["S"];

        schema.Id.ShouldBe("https://example.com/schemas/pet");
        schema.Dialect.ShouldBe("https://json-schema.org/draft/2020-12/schema");
        schema.Anchor.ShouldBe("pet");
        schema.DynamicAnchor.ShouldBe("node");

        var emitted = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        emitted.ShouldContain("\"$id\"", Case.Sensitive);
        emitted.ShouldContain("\"$anchor\"", Case.Sensitive);
    }

    private static OpenApiDocument WithSchema(OpenApiSchema schema)
    {
        var document = new OpenApiDocument
        {
            SpecVersion = OpenApiSpecVersion.V3_1,
            Info = new OpenApiInfo { Title = "t", Version = "1.0.0" },
            Components = new OpenApiComponents()
        };
        document.Components.Schemas["S"] = schema;
        return document;
    }
}
