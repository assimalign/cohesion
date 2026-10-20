using System;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Identifies a deferred migration step independently of a running engine.</summary>
public sealed class SqlCatalogMigrationStep
{
    /// <summary>Initializes a step's durable identity and position.</summary>
    /// <param name="kind">The migration operation kind.</param>
    /// <param name="table">The table's name.</param>
    /// <param name="objectName">The altered object's name.</param>
    /// <param name="position">The one-based position, after inline steps.</param>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The position is not positive.</exception>
    public SqlCatalogMigrationStep(string kind, string table, string objectName, int position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(position);
        Kind = kind;
        Table = table;
        ObjectName = objectName;
        Position = position;
    }

    /// <summary>Gets the operation kind.</summary>
    public string Kind { get; }
    /// <summary>Gets the table name.</summary>
    public string Table { get; }
    /// <summary>Gets the altered object's name.</summary>
    public string ObjectName { get; }
    /// <summary>Gets the one-based position in the migration.</summary>
    public int Position { get; }
}
