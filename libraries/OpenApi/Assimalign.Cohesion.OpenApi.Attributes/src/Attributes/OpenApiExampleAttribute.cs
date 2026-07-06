using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares a named example, attachable to an operation, parameter, or schema-bearing member. The
/// example value is carried as a serialized string so the attribute stays a compile-time constant.
/// </summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true,
    Inherited = false)]
public sealed class OpenApiExampleAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiExampleAttribute"/> class.
    /// </summary>
    /// <param name="name">The unique example name within its container.</param>
    public OpenApiExampleAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Gets the example name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets a short summary of the example.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a long description of the example. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the example value as a JSON or plain string. Mutually exclusive with <see cref="ExternalValue"/>.</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets a URI identifying an external example. Mutually exclusive with <see cref="Value"/>.</summary>
    public string? ExternalValue { get; set; }
}
