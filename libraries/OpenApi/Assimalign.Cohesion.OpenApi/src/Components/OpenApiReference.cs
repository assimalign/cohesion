namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A reference to other components in the description, internally or externally. See the "Reference Object"
/// section of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// The Reference Object does not allow arbitrary specification extensions. The <see cref="Summary"/> and
/// <see cref="Description"/> sibling fields are only valid from OpenAPI 3.1 onward and, when present,
/// override the corresponding fields of the referenced object.
/// </remarks>
public sealed class OpenApiReference : IOpenApiElement
{
    /// <summary>Gets or sets the reference identifier (the <c>$ref</c> value), for example <c>#/components/schemas/Pet</c>. Required by the specification.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>Gets or sets a short summary that overrides the referenced object's summary (OpenAPI 3.1+).</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a description that overrides the referenced object's description (OpenAPI 3.1+). CommonMark syntax may be used.</summary>
    public string? Description { get; set; }
}
