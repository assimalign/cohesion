using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares that a class or struct describes an OpenAPI schema component. The component name defaults to
/// the type name unless <see cref="Name"/> is set.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class OpenApiSchemaAttribute : Attribute
{
    /// <summary>Gets or sets the component name. Defaults to the annotated type's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the schema title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the schema description. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the instance type of the schema. Defaults to <see cref="OpenApi.SchemaType.Object"/>.</summary>
    public SchemaType Type { get; set; } = SchemaType.Object;

    /// <summary>Gets or sets a value indicating whether the schema is deprecated.</summary>
    public bool Deprecated { get; set; }
}
