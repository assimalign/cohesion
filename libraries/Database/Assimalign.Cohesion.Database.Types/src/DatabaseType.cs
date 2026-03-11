namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Represents a model-agnostic database column or field data type.
/// </summary>
public enum DatabaseType : byte
{
    /// <summary>Null or unknown type.</summary>
    Null = 0,
    /// <summary>Boolean true/false.</summary>
    Boolean,
    /// <summary>8-bit signed integer.</summary>
    Int8,
    /// <summary>16-bit signed integer.</summary>
    Int16,
    /// <summary>32-bit signed integer.</summary>
    Int32,
    /// <summary>64-bit signed integer.</summary>
    Int64,
    /// <summary>32-bit IEEE 754 floating point.</summary>
    Float32,
    /// <summary>64-bit IEEE 754 floating point.</summary>
    Float64,
    /// <summary>Arbitrary-precision decimal.</summary>
    Decimal,
    /// <summary>Variable-length Unicode string.</summary>
    String,
    /// <summary>Variable-length binary data.</summary>
    Binary,
    /// <summary>Date without time component.</summary>
    Date,
    /// <summary>Time without date component.</summary>
    Time,
    /// <summary>Date and time without timezone.</summary>
    DateTime,
    /// <summary>Date and time with timezone offset.</summary>
    DateTimeOffset,
    /// <summary>Duration or interval.</summary>
    TimeSpan,
    /// <summary>Universally unique identifier.</summary>
    Guid,
    /// <summary>JSON text data.</summary>
    Json,
    /// <summary>JSON binary data.</summary>
    JsonBinary,
}
