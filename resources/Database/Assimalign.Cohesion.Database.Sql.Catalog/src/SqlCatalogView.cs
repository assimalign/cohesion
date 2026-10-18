using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>An immutable persisted, read-only SQL view definition.</summary>
public sealed class SqlCatalogView
{
    /// <summary>Creates view metadata with frozen names and types and resolved dependencies.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The relation name.</param>
    /// <param name="definition">The canonical query with expanded projection stars.</param>
    /// <param name="columns">The frozen output names, types, domains and collations. Nullability is derived on binding.</param>
    /// <param name="tableDependencies">The identities of stored tables read directly by the query.</param>
    /// <param name="dependencies">Resolved view, column, function and domain identities.</param>
    /// <param name="functionCalls">Function identities in query traversal order.</param>
    /// <param name="owner">The durable object owner.</param>
    /// <param name="owningSchema">The compiled schema identity for a schema-owned view.</param>
    /// <exception cref="ArgumentException">A name, definition, column or dependency is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    public SqlCatalogView(string schema, string name, string definition, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<ulong> tableDependencies, IReadOnlyList<string> dependencies, IReadOnlyList<string> functionCalls,
        DatabaseObjectOwner owner = DatabaseObjectOwner.Adhoc, string? owningSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(tableDependencies);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(functionCalls);
        if (columns.Count == 0 || columns.Any(column => column is null) ||
            columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count ||
            dependencies.Any(string.IsNullOrWhiteSpace) || functionCalls.Any(string.IsNullOrWhiteSpace) || tableDependencies.Contains(0UL))
        { throw new ArgumentException(ErrorMessages.ViewMetadataInvalid); }
        SqlCatalogOwnership.Validate(owner, owningSchema);
        Schema = schema; Name = name; Definition = definition; Owner = owner; OwningSchema = owningSchema;
        Columns = Array.AsReadOnly(columns.Select(column => new SqlCatalogColumn(column.Name, column.Type,
            collation: column.Collation, domainName: column.DomainName)).ToArray());
        TableDependencies = Array.AsReadOnly(tableDependencies.Distinct().ToArray());
        Dependencies = Array.AsReadOnly(dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        FunctionCalls = Array.AsReadOnly(functionCalls.ToArray());
    }
    /// <summary>Gets the SQL namespace.</summary>
    public string Schema { get; }
    /// <summary>Gets the relation name.</summary>
    public string Name { get; }
    /// <summary>Gets the canonical body, with projection stars expanded.</summary>
    public string Definition { get; }
    /// <summary>Gets the frozen output contract; nullability is computed when bound.</summary>
    public IReadOnlyList<SqlCatalogColumn> Columns { get; }
    /// <summary>Gets the stored table identities the view reads directly.</summary>
    public IReadOnlyList<ulong> TableDependencies { get; }
    /// <summary>Gets resolved view, column, function and domain dependencies.</summary>
    public IReadOnlyList<string> Dependencies { get; }
    /// <summary>Gets the ordered resolved function identities.</summary>
    public IReadOnlyList<string> FunctionCalls { get; }
    /// <summary>Gets the durable owner.</summary>
    public DatabaseObjectOwner Owner { get; }
    /// <summary>Gets the owning compiled schema, or null for an ad-hoc view.</summary>
    public string? OwningSchema { get; }
}
