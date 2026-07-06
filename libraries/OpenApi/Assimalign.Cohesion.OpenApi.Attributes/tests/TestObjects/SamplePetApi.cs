namespace Assimalign.Cohesion.OpenApi.Attributes.Tests;

/// <summary>A schema model annotated for OpenApi generation.</summary>
[OpenApiSchema(Description = "A pet in the store.")]
internal sealed class Pet
{
    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    public long Id { get; set; }

    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.String)]
    public string Name { get; set; } = string.Empty;

    [OpenApiSchemaProperty(Name = "tag", SchemaType = OpenApiSchemaKind.String, Nullable = true)]
    public string? Category { get; set; }
}

/// <summary>A representative annotated endpoint surface.</summary>
[OpenApiTag("pets", Description = "Pet operations")]
internal static class SamplePetApi
{
    [OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet", Summary = "Get a pet", Tags = new[] { "pets" })]
    [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    [OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(Pet))]
    [OpenApiResponse(404, Description = "Not found")]
    public static void GetPet()
    {
    }
}
