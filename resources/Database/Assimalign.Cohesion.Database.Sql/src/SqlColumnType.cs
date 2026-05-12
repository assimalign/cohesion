namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Represents SQL-specific column data types.
/// </summary>
public enum SqlColumnType : byte
{
    /// <summary>Boolean type.</summary>
    Boolean,
    /// <summary>16-bit signed integer.</summary>
    SmallInt,
    /// <summary>32-bit signed integer.</summary>
    Integer,
    /// <summary>64-bit signed integer.</summary>
    BigInt,
    /// <summary>32-bit IEEE 754 floating point.</summary>
    Real,
    /// <summary>64-bit IEEE 754 floating point.</summary>
    DoublePrecision,
    /// <summary>Arbitrary-precision decimal.</summary>
    Numeric,
    /// <summary>Variable-length text.</summary>
    Text,
    /// <summary>Variable-length character string with maximum length.</summary>
    Varchar,
    /// <summary>Fixed-length character string.</summary>
    Char,
    /// <summary>Variable-length binary data.</summary>
    Bytea,
    /// <summary>Date without time component.</summary>
    Date,
    /// <summary>Time without date component.</summary>
    Time,
    /// <summary>Date and time without timezone.</summary>
    Timestamp,
    /// <summary>Date and time with timezone.</summary>
    TimestampTz,
    /// <summary>Time interval.</summary>
    Interval,
    /// <summary>Universally unique identifier.</summary>
    Uuid,
    /// <summary>JSON text data.</summary>
    Json,
    /// <summary>JSON binary data.</summary>
    Jsonb,
    /// <summary>Null type.</summary>
    Null,
}

/// <summary>
/// Defines a SQL column's data type with optional length/precision constraints.
/// </summary>
public sealed class SqlDataType
{
    /// <summary>
    /// Initializes a new <see cref="SqlDataType"/> with the specified type and optional constraints.
    /// </summary>
    /// <param name="type">The SQL column type.</param>
    /// <param name="length">The maximum length for character types.</param>
    /// <param name="precision">The precision for numeric types.</param>
    /// <param name="scale">The scale for numeric types.</param>
    public SqlDataType(SqlColumnType type, int? length = null, int? precision = null, int? scale = null)
    {
        Type = type;
        Length = length;
        Precision = precision;
        Scale = scale;
    }

    /// <summary>Gets the SQL column type.</summary>
    public SqlColumnType Type { get; }

    /// <summary>Gets the maximum length for character types.</summary>
    public int? Length { get; }

    /// <summary>Gets the precision for numeric types.</summary>
    public int? Precision { get; }

    /// <summary>Gets the scale for numeric types.</summary>
    public int? Scale { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Type switch
        {
            SqlColumnType.Varchar when Length.HasValue => $"varchar({Length})",
            SqlColumnType.Char when Length.HasValue => $"char({Length})",
            SqlColumnType.Numeric when Precision.HasValue && Scale.HasValue => $"numeric({Precision},{Scale})",
            SqlColumnType.Numeric when Precision.HasValue => $"numeric({Precision})",
            _ => Type.ToString().ToLowerInvariant()
        };
    }
}
