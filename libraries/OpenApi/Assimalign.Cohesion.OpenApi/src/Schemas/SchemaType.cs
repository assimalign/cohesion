namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The JSON Schema instance type used by an <see cref="OpenApiSchema"/>.
/// </summary>
public enum SchemaType
{
    /// <summary>A boolean value.</summary>
    Boolean,

    /// <summary>An object value.</summary>
    Object,

    /// <summary>An array value.</summary>
    Array,

    /// <summary>Any numeric value.</summary>
    Number,

    /// <summary>A string value.</summary>
    String,

    /// <summary>An integral numeric value.</summary>
    Integer,

    /// <summary>The null value. Only valid as part of a type array in OpenAPI 3.1+.</summary>
    Null
}
