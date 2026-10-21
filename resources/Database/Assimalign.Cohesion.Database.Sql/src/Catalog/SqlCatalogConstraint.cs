using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

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
    /// <param name="checkExpression">
    /// The SQL Boolean expression for a check constraint, as the canonical text the SQL engine
    /// renders from the parsed predicate. The catalog stores it as an opaque string.
    /// </param>
    /// <param name="checkFunctionCalls">Resolved normalized function signatures in expression preorder, retaining repeated calls.</param>
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
        SqlCatalogReferentialAction onDelete = SqlCatalogReferentialAction.Restrict,
        IReadOnlyList<string>? checkFunctionCalls = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (kind is not SqlCatalogConstraintKind.Reference and not SqlCatalogConstraintKind.Check ||
            onDelete is < SqlCatalogReferentialAction.Restrict or > SqlCatalogReferentialAction.SetDefault)
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
        CheckFunctionCalls = Array.AsReadOnly((checkFunctionCalls ?? []).ToArray());
        if (CheckFunctionCalls.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A check function identity cannot be blank.", nameof(checkFunctionCalls));
        }
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
    /// <summary>
    /// Gets the persisted SQL Boolean expression, when applicable: canonical SQL rendered from
    /// the parsed predicate (for example <c>qty &gt; 0 AND qty &lt; 100</c>), never the text as
    /// the user wrote it.
    /// </summary>
    public string? CheckExpression { get; }
    /// <summary>Gets each CHECK call's resolved normalized signature in expression preorder.</summary>
    public IReadOnlyList<string> CheckFunctionCalls { get; }
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
