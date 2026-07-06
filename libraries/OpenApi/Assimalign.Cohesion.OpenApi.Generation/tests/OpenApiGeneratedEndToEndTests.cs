using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Generated;
using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Generation.Tests;

public class OpenApiGeneratedEndToEndTests
{
    private static OpenApiGenerationInput FromGenerated() => new()
    {
        Operations = OpenApiMetadataRegistry.Operations,
        Schemas = OpenApiMetadataRegistry.Schemas,
        Tags = OpenApiMetadataRegistry.Tags,
        SecuritySchemes = OpenApiMetadataRegistry.SecuritySchemes
    };

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - E2E: the source generator discovers the annotated sample")]
    public void Generated_Registry_DiscoversSample()
    {
        // The registry is emitted by the source generator from this project's annotated sample types,
        // with no runtime reflection.
        OpenApiMetadataRegistry.Operations.ShouldContain(o => o.OperationId == "getGeneratedPet");
        OpenApiMetadataRegistry.Schemas.ShouldContain(s => s.Name == "GeneratedPet");
        OpenApiMetadataRegistry.Tags.ShouldContain(t => t.Name == "pets");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Generation] - E2E: generated metadata produces a valid, serializable document")]
    public void Generated_Metadata_ProducesValidDocument()
    {
        var document = OpenApiDocumentGenerator.Generate(
            FromGenerated(),
            new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Petstore", ApiVersion = "1.0.0" });

        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.OperationId.ShouldBe("getGeneratedPet");
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/GeneratedPet");
        document.Components!.Schemas["GeneratedPet"].Required.ShouldBe(["Id", "Name"]);

        document.Validate().IsValid.ShouldBeTrue();

        var json = document.ToJson(indented: false);
        OpenApiJson.Parse(json).ToJson(indented: false).ShouldBe(json);
    }
}
