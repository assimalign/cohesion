using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

public class OpenApiJsonRoundTripTests
{
    [Theory(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: core structure preserved per version")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public void RoundTrip_CoreStructure_PreservedAcrossVersions(OpenApiSpecVersion version)
    {
        var document = BuildSampleDocument(version);

        var json = document.ToJson(version);
        var round = OpenApiJson.Parse(json);

        round.SpecVersion.ShouldBe(version);
        round.Info.Title.ShouldBe("Pets API");
        round.Info.Version.ShouldBe("1.0.0");

        round.Paths.ShouldNotBeNull();
        round.Paths!.Items.ShouldContainKey("/pets/{id}");

        var operation = round.Paths.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.ShouldBe("getPet");
        operation.Parameters.ShouldHaveSingleItem();
        operation.Parameters[0].Name.ShouldBe("id");
        operation.Parameters[0].In.ShouldBe(ParameterLocation.Path);
        operation.Parameters[0].Required.ShouldBeTrue();

        operation.Responses.ShouldNotBeNull();
        operation.Responses!.Items.ShouldContainKey("200");
        operation.Responses.Items["200"].Description.ShouldBe("A single pet.");

        round.Components.ShouldNotBeNull();
        round.Components!.Schemas.ShouldContainKey("Pet");
        round.Components.Schemas["Pet"].Properties.ShouldContainKey("id");
        round.Components.Schemas["Pet"].Properties.ShouldContainKey("name");
        round.Components.Schemas["Pet"].Required.ShouldContain("id");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: internal $ref preserved")]
    public void RoundTrip_Reference_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        var content = round.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Content;
        content.ShouldContainKey("application/json");
        var schema = content["application/json"].Schema;
        schema.ShouldNotBeNull();
        schema!.Reference.ShouldNotBeNull();
        schema.Reference!.Ref.ShouldBe("#/components/schemas/Pet");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: specification extensions preserved")]
    public void RoundTrip_Extensions_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);
        document.Extensions["x-vendor-id"] = "acme";

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        round.Extensions.ShouldContainKey("x-vendor-id");
        ((OpenApiValueNode)round.Extensions["x-vendor-id"]).GetString().ShouldBe("acme");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: security requirement preserved")]
    public void RoundTrip_Security_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        round.Security.ShouldHaveSingleItem();
        round.Security[0].Schemes.ShouldContainKey("api_key");
        round.Components!.SecuritySchemes.ShouldContainKey("api_key");
        round.Components.SecuritySchemes["api_key"].Type.ShouldBe(SecuritySchemeType.ApiKey);
    }

    private static OpenApiDocument BuildSampleDocument(OpenApiSpecVersion version)
    {
        var document = new OpenApiDocument
        {
            SpecVersion = version,
            Info = new OpenApiInfo
            {
                Title = "Pets API",
                Version = "1.0.0",
                Description = "A sample API."
            }
        };

        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com" });

        var pet = new OpenApiSchema { Type = SchemaType.Object };
        pet.Properties["id"] = new OpenApiSchema { Type = SchemaType.Integer, Format = "int64" };
        pet.Properties["name"] = new OpenApiSchema { Type = SchemaType.String };
        pet.Required.Add("id");

        document.Components = new OpenApiComponents();
        document.Components.Schemas["Pet"] = pet;
        document.Components.SecuritySchemes["api_key"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = "api_key",
            In = ParameterLocation.Header
        };

        var operation = new OpenApiOperation { OperationId = "getPet" };
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "id",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = SchemaType.Integer, Format = "int64" }
        });

        var okResponse = new OpenApiResponse { Description = "A single pet." };
        okResponse.Content["application/json"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema { Reference = new OpenApiReference { Ref = "#/components/schemas/Pet" } }
        };

        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = okResponse;

        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;

        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets/{id}"] = pathItem;

        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes["api_key"] = new System.Collections.Generic.List<string>();
        document.Security.Add(requirement);

        return document;
    }
}
