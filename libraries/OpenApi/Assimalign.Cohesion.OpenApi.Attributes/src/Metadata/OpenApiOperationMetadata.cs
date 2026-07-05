using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for an OpenAPI operation, produced from an
/// <see cref="OpenApiOperationAttribute"/> and its associated parameter, request-body, response, and
/// security attributes. This is the unit the source generator emits per annotated endpoint.
/// </summary>
public sealed class OpenApiOperationMetadata
{
    /// <summary>Gets the HTTP method.</summary>
    public required OperationType Method { get; init; }

    /// <summary>Gets the path template.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the operation identifier.</summary>
    public string? OperationId { get; init; }

    /// <summary>Gets a short summary of the operation.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets a verbose description of the operation.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the grouping tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets a value indicating whether the operation is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets the operation parameters.</summary>
    public IReadOnlyList<OpenApiParameterMetadata> Parameters { get; init; } = [];

    /// <summary>Gets the request body metadata, if any.</summary>
    public OpenApiRequestBodyMetadata? RequestBody { get; init; }

    /// <summary>Gets the response metadata.</summary>
    public IReadOnlyList<OpenApiResponseMetadata> Responses { get; init; } = [];

    /// <summary>Gets the operation-level security requirements.</summary>
    public IReadOnlyList<OpenApiSecurityRequirementMetadata> Security { get; init; } = [];
}
