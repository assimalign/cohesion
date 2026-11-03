using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable scalar property declaration in a graph element type.</summary>
public sealed class GraphPropertyType
{
    private GraphPropertyType(string name, DatabaseType type, bool required)
        => (Name, Type, Required) = (name, type, required);

    /// <summary>Gets the ordinal, case-sensitive property name.</summary>
    public string Name { get; }
    /// <summary>Gets the scalar storage type, using the graph engine's numeric promotion rules.</summary>
    public DatabaseType Type { get; }
    /// <summary>Gets whether an absent or null value is refused (NOT NULL).</summary>
    public bool Required { get; }

    /// <summary>Creates a scalar declaration; nullable is the default.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="type">A supported graph scalar type.</param>
    /// <param name="required">Whether a non-null value is required.</param>
    /// <returns>The immutable declaration.</returns>
    /// <exception cref="ArgumentException">The name is blank or the scalar type is unsupported.</exception>
    public static GraphPropertyType Create(string name, DatabaseType type, bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Supports(type)) { throw new ArgumentException(ErrorMessages.GraphTypeScalarInvalid, nameof(type)); }
        return new(name, type, required);
    }

    internal static bool Supports(DatabaseType type) => type is DatabaseType.Boolean or DatabaseType.Int8 or
        DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or
        DatabaseType.Float64 or DatabaseType.Decimal or DatabaseType.String;
}
