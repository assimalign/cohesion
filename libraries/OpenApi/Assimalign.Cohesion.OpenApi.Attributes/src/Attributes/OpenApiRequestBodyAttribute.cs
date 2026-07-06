using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Describes the request body of an OpenAPI operation. The body schema is named either by a
/// <see cref="ModelType"/> (resolved to a component reference by type name) or by an explicit
/// <see cref="SchemaReference"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class OpenApiRequestBodyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiRequestBodyAttribute"/> class.
    /// </summary>
    /// <param name="contentType">The media type of the body, for example <c>application/json</c>.</param>
    public OpenApiRequestBodyAttribute(string contentType = "application/json")
    {
        ContentType = contentType;
    }

    /// <summary>Gets the media type of the request body.</summary>
    public string ContentType { get; }

    /// <summary>Gets or sets a description of the request body. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether the request body is required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets the CLR model type whose name resolves the body schema component. Mutually exclusive with <see cref="SchemaReference"/>.</summary>
    public Type? ModelType { get; set; }

    /// <summary>Gets or sets an explicit schema reference, for example <c>#/components/schemas/Pet</c>. Mutually exclusive with <see cref="ModelType"/>.</summary>
    public string? SchemaReference { get; set; }
}
