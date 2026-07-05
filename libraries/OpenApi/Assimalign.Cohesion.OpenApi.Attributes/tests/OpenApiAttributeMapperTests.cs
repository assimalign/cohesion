using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Attributes.Tests;

public class OpenApiAttributeMapperTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: an annotated endpoint produces stable operation metadata")]
    public void Map_AnnotatedEndpoint_ProducesMetadata()
    {
        var method = typeof(SamplePetApi).GetMethod(nameof(SamplePetApi.GetPet))!;
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        var metadata = OpenApiAttributeMapper.MapOperation(
            method.GetCustomAttribute<OpenApiOperationAttribute>()!,
            method.GetCustomAttributes<OpenApiParameterAttribute>(),
            method.GetCustomAttribute<OpenApiRequestBodyAttribute>(),
            method.GetCustomAttributes<OpenApiResponseAttribute>(),
            method.GetCustomAttributes<OpenApiSecurityRequirementAttribute>(),
            diagnostics);

        metadata.Method.ShouldBe(OperationType.Get);
        metadata.Path.ShouldBe("/pets/{id}");
        metadata.OperationId.ShouldBe("getPet");
        metadata.Summary.ShouldBe("Get a pet");
        metadata.Tags.ShouldBe(["pets"]);

        metadata.Parameters.Count.ShouldBe(1);
        metadata.Parameters[0].Name.ShouldBe("id");
        metadata.Parameters[0].In.ShouldBe(ParameterLocation.Path);
        metadata.Parameters[0].Required.ShouldBeTrue();
        metadata.Parameters[0].SchemaType.ShouldBe(SchemaType.Integer);

        metadata.Responses.Count.ShouldBe(2);
        var ok = metadata.Responses.Single(r => r.StatusCode == "200");
        ok.SchemaReference.ShouldBe("#/components/schemas/Pet");
        ok.ContentType.ShouldBe("application/json");

        diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a schema type produces property metadata")]
    public void Map_Schema_ProducesPropertyMetadata()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();
        var schemaAttribute = typeof(Pet).GetCustomAttribute<OpenApiSchemaAttribute>()!;
        var properties = typeof(Pet).GetProperties()
            .Select(p => (p.Name, Attribute: p.GetCustomAttribute<OpenApiSchemaPropertyAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Name, x.Attribute!));

        var metadata = OpenApiAttributeMapper.MapSchema(schemaAttribute, nameof(Pet), properties, diagnostics);

        metadata.Name.ShouldBe("Pet");
        metadata.Type.ShouldBe(SchemaType.Object);
        metadata.Properties.Count.ShouldBe(3);
        metadata.Properties.Single(p => p.Name == "Id").Required.ShouldBeTrue();
        metadata.Properties.Single(p => p.Name == "tag").Nullable.ShouldBeTrue();
        diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a model type resolves to a component reference")]
    public void ResolveSchemaReference_ByTypeName()
    {
        OpenApiAttributeMapper.ResolveSchemaReference(typeof(Pet)).ShouldBe("#/components/schemas/Pet");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a path parameter not marked required is corrected with a diagnostic")]
    public void Map_PathParameterNotRequired_Corrected()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        var metadata = OpenApiAttributeMapper.MapParameter(
            new OpenApiParameterAttribute("id", ParameterLocation.Path) { Required = false }, diagnostics);

        metadata.Required.ShouldBeTrue();
        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.PathParameterRequired && d.Severity == OpenApiMetadataSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a body with both a model type and a reference is a diagnostic")]
    public void Map_AmbiguousSchema_ReportsDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        OpenApiAttributeMapper.MapResponse(
            new OpenApiResponseAttribute(200) { ModelType = typeof(Pet), SchemaReference = "#/components/schemas/Other" },
            diagnostics);

        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.AmbiguousSchema && d.Severity == OpenApiMetadataSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: an example with both value forms is a diagnostic")]
    public void Map_AmbiguousExample_ReportsDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        OpenApiAttributeMapper.MapExample(
            new OpenApiExampleAttribute("sample") { Value = "{}", ExternalValue = "https://example.com/e.json" },
            diagnostics);

        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.AmbiguousExample);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: an example with no value is a diagnostic")]
    public void Map_EmptyExample_ReportsDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        OpenApiAttributeMapper.MapExample(new OpenApiExampleAttribute("sample"), diagnostics);

        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.EmptyExample && d.Severity == OpenApiMetadataSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: an incomplete API key scheme is a diagnostic")]
    public void Map_IncompleteApiKey_ReportsDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        OpenApiAttributeMapper.MapSecurityScheme(
            new OpenApiSecuritySchemeAttribute("key", SecuritySchemeType.ApiKey), diagnostics);

        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.IncompleteApiKey);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a complete API key scheme maps cleanly")]
    public void Map_CompleteApiKey_NoDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        var metadata = OpenApiAttributeMapper.MapSecurityScheme(
            new OpenApiSecuritySchemeAttribute("key", SecuritySchemeType.ApiKey) { ParameterName = "X-Key", In = ParameterLocation.Header },
            diagnostics);

        metadata.ParameterName.ShouldBe("X-Key");
        metadata.In.ShouldBe(ParameterLocation.Header);
        diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: an operation with an empty path is a diagnostic")]
    public void Map_EmptyPath_ReportsDiagnostic()
    {
        var diagnostics = new List<OpenApiMetadataDiagnostic>();

        OpenApiAttributeMapper.MapOperation(
            new OpenApiOperationAttribute(OperationType.Get, string.Empty), null, null, null, null, diagnostics);

        diagnostics.ShouldContain(d => d.Code == OpenApiMetadataDiagnosticCodes.MissingPath && d.Severity == OpenApiMetadataSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Attributes] - Map: a tag attribute maps all 3.2 fields")]
    public void Map_Tag_MapsFields()
    {
        var metadata = OpenApiAttributeMapper.MapTag(
            new OpenApiTagAttribute("pets") { Description = "d", Summary = "s", Parent = "root", Kind = "nav" });

        metadata.Name.ShouldBe("pets");
        metadata.Summary.ShouldBe("s");
        metadata.Parent.ShouldBe("root");
        metadata.Kind.ShouldBe("nav");
    }
}
