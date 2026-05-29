using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

/// <summary>
/// Builders for documents used across the validation tests.
/// </summary>
internal static class SampleDocuments
{
    /// <summary>
    /// Builds a fully valid document that should produce no diagnostics from the default pipeline.
    /// </summary>
    /// <param name="version">The target version.</param>
    /// <returns>A valid document.</returns>
    internal static OpenApiDocument CreateValid(OpenApiSpecVersion version)
    {
        var document = new OpenApiDocument
        {
            SpecVersion = version,
            Info = new OpenApiInfo { Title = "Pets API", Version = "1.0.0" }
        };

        document.Components = new OpenApiComponents();
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
            Schema = new OpenApiSchema { Type = SchemaType.Integer }
        });
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Description = "A single pet." };

        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;

        document.Paths = new OpenApiPaths();
        document.Paths.Items["/pets/{id}"] = pathItem;

        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes["api_key"] = new List<string>();
        document.Security.Add(requirement);

        return document;
    }
}
