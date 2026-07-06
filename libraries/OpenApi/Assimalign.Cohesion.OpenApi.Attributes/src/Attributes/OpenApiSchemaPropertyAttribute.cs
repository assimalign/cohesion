using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Describes a property of a schema component. Applied to a property or field of a type carrying
/// <see cref="OpenApiSchemaAttribute"/>. The property name defaults to the member name.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class OpenApiSchemaPropertyAttribute : Attribute
{
    /// <summary>Gets or sets the property name. Defaults to the annotated member's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the property description. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether the property is required.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a value indicating whether the property value may be null.</summary>
    public bool Nullable { get; set; }

    /// <summary>Gets or sets the scalar schema type of the property. Leave <see cref="OpenApiSchemaKind.Unspecified"/> for a referenced or complex schema.</summary>
    public OpenApiSchemaKind SchemaType { get; set; }

    /// <summary>Gets or sets the format modifier, for example <c>date-time</c>.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets a schema reference for a complex property type, for example <c>#/components/schemas/Address</c>.</summary>
    public string? SchemaReference { get; set; }
}
