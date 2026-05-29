using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A single API operation on a path. See the "Operation Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiOperation : IOpenApiExtensible
{
    /// <summary>Gets the tag names used to group this operation.</summary>
    public IList<string> Tags { get; } = new List<string>();

    /// <summary>Gets or sets a short summary of what the operation does.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a verbose explanation of the operation behavior. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets additional external documentation for this operation.</summary>
    public OpenApiExternalDocumentation? ExternalDocs { get; set; }

    /// <summary>Gets or sets a case-sensitive unique identifier for the operation.</summary>
    public string? OperationId { get; set; }

    /// <summary>Gets the parameters applicable to this operation.</summary>
    public IList<OpenApiParameter> Parameters { get; } = new List<OpenApiParameter>();

    /// <summary>Gets or sets the request body applicable to this operation.</summary>
    public OpenApiRequestBody? RequestBody { get; set; }

    /// <summary>Gets or sets the responses returned from executing this operation.</summary>
    public OpenApiResponses? Responses { get; set; }

    /// <summary>Gets the out-of-band callbacks related to this operation, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiCallback> Callbacks { get; } = new Dictionary<string, OpenApiCallback>();

    /// <summary>Gets or sets a value indicating whether this operation is deprecated.</summary>
    public bool Deprecated { get; set; }

    /// <summary>Gets the security requirements that apply to this operation, overriding the document-level requirements.</summary>
    public IList<OpenApiSecurityRequirement> Security { get; } = new List<OpenApiSecurityRequirement>();

    /// <summary>Gets the alternative servers that service this operation.</summary>
    public IList<OpenApiServer> Servers { get; } = new List<OpenApiServer>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
