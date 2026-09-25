using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Identifies a persisted relational constraint. Unique constraints use unique indexes.</summary>
public enum SqlCatalogConstraintKind : byte
{
    /// <summary>A foreign key requiring a matching referenced row.</summary>
    Reference = 0,
    /// <summary>A Boolean expression checked for each written row.</summary>
    Check,
}

/// <summary>Identifies the action applied when a referenced parent row is deleted.</summary>
public enum SqlCatalogReferentialAction : byte
{
    /// <summary>Refuses deletion while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Deletes dependent rows in the same transaction.</summary>
    Cascade,
}

/// <summary>Describes a foreign-key or check constraint persisted with its owning table.</summary>
public sealed class SqlCatalogConstraint
{
    /// <summary>Initializes an immutable constraint definition.</summary>
    /// <param name="name">The constraint name, unique within the table.</param>
    /// <param name="kind">The constraint kind.</param>
    /// <param name="columns">The constrained column names.</param>
    /// <param name="referencedSchema">The referenced SQL namespace for a foreign key.</param>
    /// <param name="referencedTable">The referenced table for a foreign key.</param>
    /// <param name="referencedColumns">The ordered referenced column names.</param>
    /// <param name="checkExpression">The SQL Boolean expression for a check constraint.</param>
    /// <param name="onDelete">The action when a referenced parent row is deleted.</param>
    /// <exception cref="ArgumentException">The definition is incomplete or inconsistent.</exception>
    public SqlCatalogConstraint(
        string name,
        SqlCatalogConstraintKind kind,
        IReadOnlyList<string> columns,
        string? referencedSchema = null,
        string? referencedTable = null,
        IReadOnlyList<string>? referencedColumns = null,
        string? checkExpression = null,
        SqlCatalogReferentialAction onDelete = SqlCatalogReferentialAction.Restrict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (kind is not SqlCatalogConstraintKind.Reference and not SqlCatalogConstraintKind.Check ||
            onDelete is not SqlCatalogReferentialAction.Restrict and not SqlCatalogReferentialAction.Cascade)
        {
            throw new ArgumentException("The constraint kind or referential action is invalid.");
        }

        if (kind == SqlCatalogConstraintKind.Reference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(referencedSchema);
            ArgumentException.ThrowIfNullOrWhiteSpace(referencedTable);
            if (columns.Count == 0 || referencedColumns is null || referencedColumns.Count != columns.Count || checkExpression is not null)
            {
                throw new ArgumentException("A foreign key must pair its columns with referenced columns and cannot contain a check expression.");
            }
        }
        else if (string.IsNullOrWhiteSpace(checkExpression) || referencedSchema is not null || referencedTable is not null ||
                 referencedColumns is { Count: > 0 } || onDelete != SqlCatalogReferentialAction.Restrict)
        {
            throw new ArgumentException("A check constraint requires an expression and cannot declare a reference or referential action.");
        }

        Name = name;
        Kind = kind;
        Columns = Snapshot(columns);
        ReferencedSchema = referencedSchema;
        ReferencedTable = referencedTable;
        ReferencedColumns = Snapshot(referencedColumns ?? Array.Empty<string>());
        CheckExpression = checkExpression;
        OnDelete = onDelete;
    }

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }
    /// <summary>Gets the constraint kind.</summary>
    public SqlCatalogConstraintKind Kind { get; }
    /// <summary>Gets the constrained column names.</summary>
    public IReadOnlyList<string> Columns { get; }
    /// <summary>Gets the referenced SQL namespace, when applicable.</summary>
    public string? ReferencedSchema { get; }
    /// <summary>Gets the referenced table, when applicable.</summary>
    public string? ReferencedTable { get; }
    /// <summary>Gets the ordered referenced column names.</summary>
    public IReadOnlyList<string> ReferencedColumns { get; }
    /// <summary>Gets the persisted SQL Boolean expression, when applicable.</summary>
    public string? CheckExpression { get; }
    /// <summary>Gets the action when a referenced parent row is deleted.</summary>
    public SqlCatalogReferentialAction OnDelete { get; }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < values.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(values[index]);
            if (!names.Add(values[index]))
            {
                throw new ArgumentException($"Constraint column '{values[index]}' is repeated.");
            }
            copy[index] = values[index];
        }
        return Array.AsReadOnly(copy);
    }
}
