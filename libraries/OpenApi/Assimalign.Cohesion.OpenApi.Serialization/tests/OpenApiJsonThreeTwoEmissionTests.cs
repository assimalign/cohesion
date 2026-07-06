using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

public class OpenApiJsonThreeTwoEmissionTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: server name emitted, omitted below 3.2")]
    public void Emit_ServerName_GatedToThreeTwo()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com", Name = "production" });

        document.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldContain("\"name\":\"production\"", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldNotContain("\"name\":\"production\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: query operation emitted, omitted below 3.2")]
    public void Emit_QueryOperation_GatedToThreeTwo()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Query] = new OpenApiOperation { OperationId = "queryPets" };
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        document.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldContain("\"query\":{", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldNotContain("\"query\":{", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: querystring parameter location round-trips")]
    public void Emit_QuerystringLocation_RoundTrips()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        var operation = new OpenApiOperation();
        operation.Parameters.Add(new OpenApiParameter { Name = "filter", In = ParameterLocation.Querystring });
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"in\":\"querystring\"", Case.Sensitive);

        var parsed = OpenApiJson.Parse(json);
        parsed.Paths!.Items["/pets"].Operations[OperationType.Get].Parameters[0].In.ShouldBe(ParameterLocation.Querystring);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: response summary emitted, omitted below 3.2")]
    public void Emit_ResponseSummary_GatedToThreeTwo()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        var operation = new OpenApiOperation();
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Summary = "The pet.", Description = "A single pet." };
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        document.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldContain("\"summary\":\"The pet.\"", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldNotContain("\"summary\":\"The pet.\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: example dataValue and serializedValue gated")]
    public void Emit_ExampleDataValues_GatedToThreeTwo()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components = new OpenApiComponents();
        document.Components.Examples["pet"] = new OpenApiExample
        {
            DataValue = new OpenApiObjectNode { ["name"] = "Rex" },
            SerializedValue = "name=Rex"
        };

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"dataValue\":{\"name\":\"Rex\"}", Case.Sensitive);
        json.ShouldContain("\"serializedValue\":\"name=Rex\"", Case.Sensitive);

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        downLevel.ShouldNotContain("dataValue", Case.Sensitive);
        downLevel.ShouldNotContain("serializedValue", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: discriminator defaultMapping gated")]
    public void Emit_DiscriminatorDefaultMapping_GatedToThreeTwo()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components = new OpenApiComponents();
        var schema = new OpenApiSchema
        {
            Discriminator = new OpenApiDiscriminator { PropertyName = "petType", DefaultMapping = "Dog" }
        };
        document.Components.Schemas["Pet"] = schema;

        document.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldContain("\"defaultMapping\":\"Dog\"", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldNotContain("defaultMapping", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: xml nodeType replaces attribute and wrapped")]
    public void Emit_XmlNodeType_ReplacesDeprecatedFlags()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components = new OpenApiComponents();
        document.Components.Schemas["Pet"] = new OpenApiSchema
        {
            Xml = new OpenApiXml { Name = "pet", NodeType = XmlNodeType.Attribute, Attribute = true }
        };

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"nodeType\":\"attribute\"", Case.Sensitive);
        json.ShouldNotContain("\"attribute\":true", Case.Sensitive);

        // Below 3.2 the deprecated boolean flags carry the intent instead.
        var downLevel = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        downLevel.ShouldNotContain("nodeType", Case.Sensitive);
        downLevel.ShouldContain("\"attribute\":true", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: oauth2MetadataUrl and deprecated gated")]
    public void Emit_SecuritySchemeThreeTwoFields_Gated()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components = new OpenApiComponents();
        document.Components.SecuritySchemes["oauth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            OAuth2MetadataUrl = "https://auth.example.com/.well-known/oauth-authorization-server",
            Deprecated = true,
            Flows = new OpenApiOAuthFlows
            {
                DeviceAuthorization = new OpenApiOAuthFlow
                {
                    DeviceAuthorizationUrl = "https://auth.example.com/device",
                    TokenUrl = "https://auth.example.com/token"
                }
            }
        };

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"oauth2MetadataUrl\"", Case.Sensitive);
        json.ShouldContain("\"deprecated\":true", Case.Sensitive);
        json.ShouldContain("\"deviceAuthorization\":{", Case.Sensitive);
        json.ShouldContain("\"deviceAuthorizationUrl\":\"https://auth.example.com/device\"", Case.Sensitive);

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        downLevel.ShouldNotContain("oauth2MetadataUrl", Case.Sensitive);
        downLevel.ShouldNotContain("\"deprecated\":true", Case.Sensitive);
        downLevel.ShouldNotContain("deviceAuthorization", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: media type streaming fields gated")]
    public void Emit_MediaTypeStreamingFields_Gated()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        var media = new OpenApiMediaType { ItemSchema = new OpenApiSchema { Type = SchemaType.Object } };
        media.PrefixEncoding.Add(new OpenApiEncoding { ContentType = "image/png" });
        media.ItemEncoding = new OpenApiEncoding { ContentType = "application/json" };

        var operation = new OpenApiOperation();
        operation.Responses = new OpenApiResponses();
        var response = new OpenApiResponse { Description = "stream" };
        response.Content["application/jsonl"] = media;
        operation.Responses.Items["200"] = response;
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"itemSchema\":{", Case.Sensitive);
        json.ShouldContain("\"prefixEncoding\":[", Case.Sensitive);
        json.ShouldContain("\"itemEncoding\":{", Case.Sensitive);

        var downLevel = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);
        downLevel.ShouldNotContain("itemSchema", Case.Sensitive);
        downLevel.ShouldNotContain("prefixEncoding", Case.Sensitive);
        downLevel.ShouldNotContain("itemEncoding", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: components mediaTypes and content references gated")]
    public void Emit_ComponentsMediaTypes_AndContentReference_Gated()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components = new OpenApiComponents();
        document.Components.MediaTypes["PetJson"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = SchemaType.Object } };

        var operation = new OpenApiOperation();
        operation.Responses = new OpenApiResponses();
        var response = new OpenApiResponse { Description = "ok" };
        response.Content["application/json"] = new OpenApiMediaType
        {
            Reference = new OpenApiReference { Ref = "#/components/mediaTypes/PetJson" }
        };
        operation.Responses.Items["200"] = response;
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        json.ShouldContain("\"mediaTypes\":{", Case.Sensitive);
        json.ShouldContain("\"$ref\":\"#/components/mediaTypes/PetJson\"", Case.Sensitive);

        // The components.mediaTypes map is version-gated away below 3.2; the reference itself is kept
        // verbatim (dangling references are a validation concern, not a serialization one).
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldNotContain("\"mediaTypes\":{", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit 3.2: full 3.2 document round-trips")]
    public void Emit_ThreeTwoDocument_RoundTrips()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Self = "https://example.com/openapi.json";
        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com", Name = "production" });

        var operation = new OpenApiOperation { OperationId = "queryPets" };
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Summary = "Result.", Description = "Query result." };
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Query] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets"] = pathItem;

        var json = document.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        var parsed = OpenApiJson.Parse(json);

        parsed.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_2);
        parsed.Self.ShouldBe("https://example.com/openapi.json");
        parsed.Servers[0].Name.ShouldBe("production");
        parsed.Paths!.Items["/pets"].Operations.ShouldContainKey(OperationType.Query);
        parsed.Paths.Items["/pets"].Operations[OperationType.Query].Responses!.Items["200"].Summary.ShouldBe("Result.");
        parsed.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldBe(json);
    }

    private static OpenApiDocument Minimal(OpenApiSpecVersion version) => new()
    {
        SpecVersion = version,
        Info = new OpenApiInfo { Title = "t", Version = "1.0.0" }
    };
}
