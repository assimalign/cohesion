using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Integration.Tests;

public class OpenApiWebIntegrationTests
{
    private static OpenApiDescriptionInfo Info => new() { Title = "Petstore", ApiVersion = "1.0.0" };

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - Web: a generated endpoint source produces a valid document")]
    public void Web_GeneratedSource_ProducesValidDocument()
    {
        // The Web path with no runtime reflection: annotated endpoints -> source-generated registry ->
        // endpoint source -> description provider -> document.
        var provider = OpenApiIntegration.CreateProvider(Info, new GeneratedEndpointSource());

        var document = provider.GetDocument(OpenApiSpecVersion.V3_1);

        document.Info.Title.ShouldBe("Petstore");
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].OperationId.ShouldBe("getPet");
        document.Components!.Schemas.ShouldContainKey("Pet");
        document.Validate().IsValid.ShouldBeTrue();

        var json = document.ToJson(indented: false);
        OpenApiJson.Parse(json).ToJson(indented: false).ShouldBe(json);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - Web: the provider targets the requested line")]
    public void Web_Provider_TargetsRequestedVersion()
    {
        var provider = OpenApiIntegration.CreateProvider(Info, new GeneratedEndpointSource());

        provider.GetDocument(OpenApiSpecVersion.V3_0).SpecVersion.ShouldBe(OpenApiSpecVersion.V3_0);
        provider.GetDocument(OpenApiSpecVersion.V3_2).SpecVersion.ShouldBe(OpenApiSpecVersion.V3_2);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - Web: multiple endpoint sources compose")]
    public void Web_MultipleSources_Compose()
    {
        var provider = OpenApiIntegration.CreateProvider(Info, new GeneratedEndpointSource(), new ExtraEndpointSource());

        var document = provider.GetDocument(OpenApiSpecVersion.V3_1);

        document.Paths!.Items.ShouldContainKey("/pets/{id}");
        document.Paths.Items.ShouldContainKey("/health");
        document.Validate().IsValid.ShouldBeTrue();
    }

    /// <summary>A second source contributing one more endpoint, to prove aggregation.</summary>
    private sealed class ExtraEndpointSource : IOpenApiEndpointSource
    {
        public IReadOnlyList<OpenApiOperationMetadata> Operations =>
        [
            new OpenApiOperationMetadata
            {
                Method = OperationType.Get,
                Path = "/health",
                OperationId = "health",
                Responses = [new OpenApiResponseMetadata { StatusCode = "200", Description = "ok" }]
            }
        ];

        public IReadOnlyList<OpenApiSchemaMetadata> Schemas => [];

        public IReadOnlyList<OpenApiTagMetadata> Tags => [];

        public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes => [];
    }
}
