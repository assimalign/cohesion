using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.Generation.Tests;

/// <summary>A schema model discovered by the source generator.</summary>
[OpenApiSchema(Description = "A pet in the store.")]
internal sealed class GeneratedPet
{
    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    public long Id { get; set; }

    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.String)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>An annotated endpoint surface discovered by the source generator.</summary>
[OpenApiTag("pets", Description = "Pet operations")]
internal static class GeneratedSampleApi
{
    [OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getGeneratedPet", Tags = new[] { "pets" })]
    [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    [OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(GeneratedPet))]
    [OpenApiResponse(404, Description = "Not found")]
    public static void GetPet()
    {
    }
}
