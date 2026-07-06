using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Generation.Tests;

public class OpenApiDocumentGeneratorTests
{
    private static OpenApiGenerationInput SampleInput() => new()
    {
        Operations =
        [
            new OpenApiOperationMetadata
            {
                Method = OperationType.Get,
                Path = "/pets/{id}",
                OperationId = "getPet",
                Summary = "Get a pet",
                Tags = ["pets"],
                Parameters =
                [
                    new OpenApiParameterMetadata { Name = "id", In = ParameterLocation.Path, Required = true, SchemaType = SchemaType.Integer, Format = "int64" }
                ],
                Responses =
                [
                    new OpenApiResponseMetadata { StatusCode = "200", Description = "The pet", ContentType = "application/json", SchemaReference = "#/components/schemas/Pet" },
                    new OpenApiResponseMetadata { StatusCode = "404", Description = "Not found" }
                ]
            }
        ],
        Schemas =
        [
            new OpenApiSchemaMetadata
            {
                Name = "Pet",
                Type = SchemaType.Object,
                Properties =
                [
                    new OpenApiSchemaPropertyMetadata { Name = "id", Required = true, SchemaType = SchemaType.Integer, Format = "int64" },
                    new OpenApiSchemaPropertyMetadata { Name = "name", Required = true, SchemaType = SchemaType.String }
                ]
            }
        ],
        Tags = [new OpenApiTagMetadata { Name = "pets", Description = "Pet operations", Summary = "Pets", Kind = "nav" }],
        SecuritySchemes = [new OpenApiSecuritySchemeMetadata { Name = "api_key", Type = SecuritySchemeType.ApiKey, ParameterName = "X-Key", In = ParameterLocation.Header }]
    };

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: metadata produces the expected model")]
    public void Generate_Metadata_ProducesModel()
    {
        var document = OpenApiDocumentGenerator.Generate(SampleInput(), new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Petstore", ApiVersion = "1.0.0" });

        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        document.Info.Title.ShouldBe("Petstore");

        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.ShouldBe("getPet");
        operation.Parameters[0].Name.ShouldBe("id");
        operation.Parameters[0].Required.ShouldBeTrue();
        operation.Parameters[0].Schema!.Type.ShouldBe(SchemaType.Integer);
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/Pet");

        document.Components!.Schemas["Pet"].Properties.Count.ShouldBe(2);
        document.Components.Schemas["Pet"].Required.ShouldBe(["id", "name"]);
        document.Components.SecuritySchemes["api_key"].Name.ShouldBe("X-Key");
        document.Tags[0].Name.ShouldBe("pets");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: a generated document passes validation")]
    public void Generate_Document_IsValid()
    {
        var document = OpenApiDocumentGenerator.Generate(SampleInput(), new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Petstore", ApiVersion = "1.0.0" });

        document.Validate().IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: a generated document serializes to JSON and YAML")]
    public void Generate_Document_Serializes()
    {
        var document = OpenApiDocumentGenerator.Generate(SampleInput(), new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Petstore", ApiVersion = "1.0.0" });

        var json = document.ToJson(indented: false);
        json.ShouldContain("\"openapi\":\"3.1.2\"", Case.Sensitive);
        OpenApiJson.Parse(json).ToJson(indented: false).ShouldBe(json);

        var yaml = document.ToYaml();
        OpenApiYaml.Parse(yaml).ToYaml().ShouldBe(yaml);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: tag 3.2 fields are dropped when targeting 3.1")]
    public void Generate_TagExtendedFields_GatedToThreeTwo()
    {
        var input = SampleInput();

        var threeOne = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "t", ApiVersion = "1" });
        threeOne.Tags[0].Summary.ShouldBeNull();
        threeOne.Tags[0].Kind.ShouldBeNull();

        var threeTwo = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_2, Title = "t", ApiVersion = "1" });
        threeTwo.Tags[0].Summary.ShouldBe("Pets");
        threeTwo.Tags[0].Kind.ShouldBe("nav");
        threeTwo.Validate().IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: a query operation is dropped below 3.2")]
    public void Generate_QueryOperation_GatedToThreeTwo()
    {
        var input = new OpenApiGenerationInput
        {
            Operations =
            [
                new OpenApiOperationMetadata { Method = OperationType.Query, Path = "/pets", OperationId = "queryPets", Responses = [new OpenApiResponseMetadata { StatusCode = "200", Description = "ok" }] }
            ]
        };

        var threeOne = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "t", ApiVersion = "1" });
        threeOne.Paths.ShouldBeNull();

        var threeTwo = OpenApiDocumentGenerator.Generate(input, new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_2, Title = "t", ApiVersion = "1" });
        threeTwo.Paths!.Items["/pets"].Operations.ShouldContainKey(OperationType.Query);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - Generate: the full attribute-to-model path composes")]
    public void Generate_FromAttributeMapper_Composes()
    {
        // Exercise the attribute mapper and feed its metadata into the generator (the runtime analogue
        // of the source generator's compile-time path).
        var diagnostics = new List<OpenApiMetadataDiagnostic>();
        var operation = OpenApiAttributeMapper.MapOperation(
            new Attributes.OpenApiOperationAttribute(OperationType.Get, "/ping") { OperationId = "ping" },
            null, null,
            [new Attributes.OpenApiResponseAttribute(200) { Description = "pong" }],
            null, diagnostics);

        var document = OpenApiDocumentGenerator.Generate(
            new OpenApiGenerationInput { Operations = [operation] },
            new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "t", ApiVersion = "1" });

        diagnostics.ShouldBeEmpty();
        document.Paths!.Items["/ping"].Operations[OperationType.Get].OperationId.ShouldBe("ping");
        document.Validate().IsValid.ShouldBeTrue();
    }
}
