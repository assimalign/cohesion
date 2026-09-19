using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The catalog's description of one column: name, shared type identity, nullability,
/// optional default literal, and optional collation override.
/// </summary>
public sealed class SqlCatalogColumn
{
    /// <summary>
    /// Initializes a new column description.
    /// </summary>
    /// <param name="name">The column name, unique within its table.</param>
    /// <param name="type">The shared type identity and constraints.</param>
    /// <param name="isNullable">Whether the column accepts nulls.</param>
    /// <param name="defaultLiteral">The default value literal text, when declared.</param>
    /// <param name="collation">The string collation override; null inherits the database default.</param>
    public SqlCatalogColumn(string name, DatabaseTypeInfo type, bool isNullable = true, string? defaultLiteral = null, Collation? collation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        if (collation is not null && type.Type != DatabaseType.String)
        {
            throw new ArgumentException("Only string columns may declare a collation.", nameof(collation));
        }

        Name = name;
        Type = type;
        IsNullable = isNullable;
        DefaultLiteral = defaultLiteral;
        Collation = collation;
    }

    /// <summary>
    /// Gets the column name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the shared type identity and constraints.
    /// </summary>
    public DatabaseTypeInfo Type { get; }

    /// <summary>
    /// Gets a value indicating whether the column accepts nulls.
    /// </summary>
    public bool IsNullable { get; }

    /// <summary>
    /// Gets the default value literal text, when one was declared.
    /// </summary>
    public string? DefaultLiteral { get; }

    /// <summary>
    /// Gets the declared string collation, or null to inherit the database default.
    /// </summary>
    public Collation? Collation { get; }
}
