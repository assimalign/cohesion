namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Scalar storage types supported by graph property constraints.</summary>
public enum GqlSchemaPropertyType
{
    /// <summary>A Boolean value.</summary>
    Boolean,
    /// <summary>A signed eight-bit integer.</summary>
    Int8,
    /// <summary>A signed sixteen-bit integer.</summary>
    Int16,
    /// <summary>A signed thirty-two-bit integer.</summary>
    Int32,
    /// <summary>A signed sixty-four-bit integer.</summary>
    Int64,
    /// <summary>A single-precision number.</summary>
    Float32,
    /// <summary>A double-precision number.</summary>
    Float64,
    /// <summary>A decimal number.</summary>
    Decimal,
    /// <summary>A Unicode string.</summary>
    String,
}
