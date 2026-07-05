using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares a document-level tag. Applied to an assembly or a class to register a tag with its
/// documentation metadata. The 3.2-only <see cref="Summary"/>, <see cref="Parent"/>, and
/// <see cref="Kind"/> fields are ignored by generation when targeting an earlier line.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiTagAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiTagAttribute"/> class.
    /// </summary>
    /// <param name="name">The tag name.</param>
    public OpenApiTagAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Gets the tag name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the tag description. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a short summary of the tag (OpenAPI 3.2+).</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets the parent tag name, forming a hierarchy (OpenAPI 3.2+).</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets the machine-readable tag kind, for example <c>nav</c> (OpenAPI 3.2+).</summary>
    public string? Kind { get; set; }
}
