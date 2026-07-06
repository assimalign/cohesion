using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;

namespace Assimalign.Cohesion.OpenApi.Integration.Tests;

/// <summary>A schema model a Web layer would expose.</summary>
[OpenApiSchema(Description = "A pet.")]
internal sealed class Pet
{
    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    public long Id { get; set; }

    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.String)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>A representative annotated Web endpoint surface, discovered by the source generator.</summary>
[OpenApiTag("pets", Description = "Pet operations")]
internal static class SampleWebApi
{
    [OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet", Tags = new[] { "pets" })]
    [OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    [OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(Pet))]
    public static void GetPet()
    {
    }
}
