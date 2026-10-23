using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// A consistent, read-only capture of a SQL catalog's namespace, table, view and index descriptions, taken by
/// <see cref="SqlCatalog.CaptureSnapshot"/>.
/// </summary>
/// <remarks>
/// All descriptions and the default collation are captured together under the catalog's
/// publication lock. Later catalog publications do not change this capture; it owns no storage
/// or disposal lifetime. <b>Shape (concrete-types plan, phase 4, #1260):</b> the former
/// <c>ISqlCatalogSnapshot</c> interface and its internal implementation collapsed into this
/// sealed type, whose constructor is internal.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlCatalogSnapshot")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogSnapshot.DebugView))]
public sealed class SqlCatalogSnapshot
{
    /// <summary>Gets the SQL functions present at capture time.</summary>
    public IReadOnlyList<SqlCatalogFunction> Functions { get; }

    /// <summary>Gets the SQL domains present at capture time.</summary>
    public IReadOnlyList<SqlCatalogDomain> Domains { get; }

    /// <summary>Gets the SQL namespaces, including empty namespaces, present at capture time.</summary>
    public IReadOnlyList<SqlCatalogNamespace> Namespaces { get; }

    /// <summary>Gets the persisted user views present at capture time.</summary>
    public IReadOnlyList<SqlCatalogView> Views { get; }

    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<SqlCatalogIndex>> _indexes;

    internal SqlCatalogSnapshot(IEnumerable<SqlCatalogTable> tables, IEnumerable<SqlCatalogIndex> indexes, Collation? defaultCollation = null,
        IEnumerable<SqlCatalogFunction>? functions = null, IEnumerable<SqlCatalogDomain>? domains = null,
        IEnumerable<SqlCatalogNamespace>? namespaces = null, IEnumerable<SqlCatalogView>? views = null)
    {
        Views = Array.AsReadOnly((views ?? []).ToArray());
        Functions = Array.AsReadOnly((functions ?? []).ToArray());
        Domains = Array.AsReadOnly((domains ?? []).ToArray());
        Namespaces = Array.AsReadOnly((namespaces ?? []).ToArray());
        DefaultCollation = defaultCollation ?? Collation.Binary;
        Tables = Array.AsReadOnly(tables.ToArray());
        _indexes = indexes.GroupBy(index => index.TableObjectId).ToDictionary(
            group => group.Key, group => (IReadOnlyList<SqlCatalogIndex>)Array.AsReadOnly(group.ToArray()));
    }

    /// <summary>Gets the table descriptions present at capture time.</summary>
    public IReadOnlyList<SqlCatalogTable> Tables { get; }

    /// <summary>Gets the database default collation at capture time.</summary>
    public Collation DefaultCollation { get; }

    /// <summary>Gets the captured index descriptions for a table.</summary>
    /// <param name="objectId">The table's catalog object identity.</param>
    /// <returns>The captured indexes, or an empty list when none exist.</returns>
    public IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong objectId)
        => _indexes.TryGetValue(objectId, out var indexes) ? indexes : Array.Empty<SqlCatalogIndex>();

    /// <summary>Finds a captured table by its case-insensitive SQL namespace and name.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="table">The captured table when found; otherwise null.</param>
    /// <returns>True when the captured directory contains the table; otherwise false.</returns>
    public bool TryGetTable(string schema, string name, out SqlCatalogTable table)
    {
        foreach (var candidate in Tables)
        {
            if (string.Equals(candidate.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                table = candidate;
                return true;
            }
        }

        table = null!;
        return false;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogSnapshot _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogSnapshot value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogFunction> Functions => _target.Functions;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogDomain> Domains => _target.Domains;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogNamespace> Namespaces => _target.Namespaces;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogView> Views => _target.Views;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable> Tables => _target.Tables;
        public global::Assimalign.Cohesion.Database.Types.Collation DefaultCollation => _target.DefaultCollation;
    }
}
