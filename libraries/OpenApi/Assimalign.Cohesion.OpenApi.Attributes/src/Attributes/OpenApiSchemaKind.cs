namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The scalar schema type an attribute optionally declares. Mirrors <see cref="SchemaType"/> with an
/// added <see cref="Unspecified"/> sentinel, because a nullable enum is not a valid attribute argument
/// type — the attribute uses this enum, and the mapper converts it to a <see cref="SchemaType"/>.
/// </summary>
public enum OpenApiSchemaKind
{
    /// <summary>No scalar schema type was declared; the mapper produces no type.</summary>
    Unspecified = 0,

    /// <summary>A boolean value. Maps to <see cref="SchemaType.Boolean"/>.</summary>
    Boolean,

    /// <summary>An object value. Maps to <see cref="SchemaType.Object"/>.</summary>
    Object,

    /// <summary>An array value. Maps to <see cref="SchemaType.Array"/>.</summary>
    Array,

    /// <summary>A numeric value. Maps to <see cref="SchemaType.Number"/>.</summary>
    Number,

    /// <summary>A string value. Maps to <see cref="SchemaType.String"/>.</summary>
    String,

    /// <summary>An integral value. Maps to <see cref="SchemaType.Integer"/>.</summary>
    Integer,

    /// <summary>The null value. Maps to <see cref="SchemaType.Null"/>.</summary>
    Null
}
