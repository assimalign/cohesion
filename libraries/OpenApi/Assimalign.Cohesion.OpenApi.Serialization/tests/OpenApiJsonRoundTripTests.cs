using FluentAssertions;
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

        round.SpecVersion.Should().Be(version);
        round.Info.Title.Should().Be("Pets API");
        round.Info.Version.Should().Be("1.0.0");

        round.Paths.Should().NotBeNull();
        round.Paths!.Items.Should().ContainKey("/pets/{id}");

        var operation = round.Paths.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.Should().Be("getPet");
        operation.Parameters.Should().ContainSingle();
        operation.Parameters[0].Name.Should().Be("id");
        operation.Parameters[0].In.Should().Be(ParameterLocation.Path);
        operation.Parameters[0].Required.Should().BeTrue();

        operation.Responses.Should().NotBeNull();
        operation.Responses!.Items.Should().ContainKey("200");
        operation.Responses.Items["200"].Description.Should().Be("A single pet.");

        round.Components.Should().NotBeNull();
        round.Components!.Schemas.Should().ContainKey("Pet");
        round.Components.Schemas["Pet"].Properties.Should().ContainKeys("id", "name");
        round.Components.Schemas["Pet"].Required.Should().Contain("id");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: internal $ref preserved")]
    public void RoundTrip_Reference_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        var content = round.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Content;
        content.Should().ContainKey("application/json");
        var schema = content["application/json"].Schema;
        schema.Should().NotBeNull();
        schema!.Reference.Should().NotBeNull();
        schema.Reference!.Ref.Should().Be("#/components/schemas/Pet");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: specification extensions preserved")]
    public void RoundTrip_Extensions_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);
        document.Extensions["x-vendor-id"] = "acme";

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        round.Extensions.Should().ContainKey("x-vendor-id");
        ((OpenApiValueNode)round.Extensions["x-vendor-id"]).GetString().Should().Be("acme");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - RoundTrip: security requirement preserved")]
    public void RoundTrip_Security_Preserved()
    {
        var document = BuildSampleDocument(OpenApiSpecVersion.V3_1);

        var round = OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_1));

        round.Security.Should().ContainSingle();
        round.Security[0].Schemes.Should().ContainKey("api_key");
        round.Components!.SecuritySchemes.Should().ContainKey("api_key");
        round.Components.SecuritySchemes["api_key"].Type.Should().Be(SecuritySchemeType.ApiKey);
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
