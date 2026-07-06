using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Describes an OpenAPI operation parameter. Applied to a method (naming the parameter explicitly) or to
/// a method parameter (taking its name from the parameter).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiParameterAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiParameterAttribute"/> class.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    public OpenApiParameterAttribute(string name, ParameterLocation location)
    {
        Name = name;
        In = location;
    }

    /// <summary>Gets the parameter name.</summary>
    public string Name { get; }

    /// <summary>Gets the parameter location.</summary>
    public ParameterLocation In { get; }

    /// <summary>Gets or sets the parameter description. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether the parameter is required. Path parameters are always required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a value indicating whether the parameter is deprecated.</summary>
    public bool Deprecated { get; set; }

    /// <summary>Gets or sets the parameter's scalar schema type. Leave <see cref="OpenApiSchemaKind.Unspecified"/> for a referenced or complex schema.</summary>
    public OpenApiSchemaKind SchemaType { get; set; }

    /// <summary>Gets or sets the format modifier for the schema type, for example <c>int64</c>.</summary>
    public string? Format { get; set; }
}
