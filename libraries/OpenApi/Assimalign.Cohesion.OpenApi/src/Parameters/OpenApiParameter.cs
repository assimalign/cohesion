using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A single operation parameter. See the "Parameter Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiParameter : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets the case-sensitive name of the parameter. Required by the specification.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the location of the parameter. Required by the specification.</summary>
    public ParameterLocation In { get; set; }

    /// <summary>Gets or sets a brief description of the parameter. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether this parameter is mandatory. Always required for path parameters.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a value indicating whether the parameter is deprecated.</summary>
    public bool Deprecated { get; set; }

    /// <summary>Gets or sets a value indicating whether an empty value is allowed for a query parameter.</summary>
    public bool AllowEmptyValue { get; set; }

    /// <summary>Gets or sets how the parameter value is serialized.</summary>
    public ParameterStyle? Style { get; set; }

    /// <summary>Gets or sets a value indicating whether array or object values generate separate parameters per item.</summary>
    public bool? Explode { get; set; }

    /// <summary>Gets or sets a value indicating whether reserved characters are allowed without percent-encoding.</summary>
    public bool AllowReserved { get; set; }

    /// <summary>Gets or sets the schema defining the type used for the parameter.</summary>
    public OpenApiSchema? Schema { get; set; }

    /// <summary>Gets or sets a single example of the parameter value.</summary>
    public OpenApiNode? Example { get; set; }

    /// <summary>Gets the examples of the parameter value, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiExample> Examples { get; } = new Dictionary<string, OpenApiExample>();

    /// <summary>Gets the content representing the parameter, keyed by media type. Mutually exclusive with <see cref="Schema"/>.</summary>
    public IDictionary<string, OpenApiMediaType> Content { get; } = new Dictionary<string, OpenApiMediaType>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}
