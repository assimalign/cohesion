namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Describes a data type with optional length and precision constraints.
/// </summary>
public sealed class DatabaseTypeInfo
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseTypeInfo"/> with the specified type and optional constraints.
    /// </summary>
    /// <param name="type">The base data type.</param>
    /// <param name="maxLength">The maximum length for string and binary types.</param>
    /// <param name="precision">The precision for decimal types.</param>
    /// <param name="scale">The scale for decimal types.</param>
    public DatabaseTypeInfo(DatabaseType type, int? maxLength = null, int? precision = null, int? scale = null)
    {
        Type = type;
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
    }

    /// <summary>
    /// Gets the base data type.
    /// </summary>
    public DatabaseType Type { get; }

    /// <summary>
    /// Gets the maximum length for string and binary types.
    /// </summary>
    public int? MaxLength { get; }

    /// <summary>
    /// Gets the precision for decimal types.
    /// </summary>
    public int? Precision { get; }

    /// <summary>
    /// Gets the scale for decimal types.
    /// </summary>
    public int? Scale { get; }
}
