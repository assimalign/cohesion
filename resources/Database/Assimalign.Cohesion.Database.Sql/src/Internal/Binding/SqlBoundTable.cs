using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One table version with its persisted expressions parsed and bound: the CHECK predicates in
/// declaration order and the value each column DEFAULT supplies. Built once per
/// <see cref="SqlCatalogTable"/> instance by <see cref="SqlBoundTableCache"/>, so the write and
/// read paths never parse catalog text or convert a DEFAULT again.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundTable")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundTable.DebugView))]
internal sealed class SqlBoundTable
{
    private readonly IReadOnlyList<SqlBoundCheck> _checks;
    private readonly Collation _defaultCollation;
    private readonly (IReadOnlyList<string> Columns, SqlBoundConstraintKey Key)[]? _foreignKeys;
    private ConditionalWeakTable<IReadOnlyList<string>, SqlBoundConstraintKey>? _keys;
    private readonly object _keySync = new();
    private int _keyBindCount;

    // The bound domain of each column by ordinal, null where the column has none; null when no column has one.
    private readonly SqlBoundDomain?[]? _domainsByOrdinal;

    /// <summary>Initializes a bound table version.</summary>
    /// <param name="table">The table version the expressions were bound against.</param>
    /// <param name="checks">The bound CHECK constraints, in declaration order.</param>
    /// <param name="defaultValues">Each column's bound DEFAULT by ordinal; null where the column declares none.</param>
    /// <param name="defaultCollation">The database collation used by key columns without an explicit collation.</param>
    /// <param name="domains">The bound domains of the columns that have one, in column order; empty when none has.</param>
    /// <param name="missingValues">Frozen defaults for stored rows that predate an appended column.</param>
    internal SqlBoundTable(SqlCatalogTable table, IReadOnlyList<SqlBoundCheck> checks, IReadOnlyList<SqlBoundExpression?> defaultValues, Collation defaultCollation,
        IReadOnlyList<SqlBoundDomain>? domains = null, IReadOnlyList<SqlBoundExpression?>? missingValues = null)
    {
        Table = table;
        _defaultCollation = defaultCollation;
        _checks = checks;
        foreach (var check in checks) { ChecksContainLike |= check.ContainsLike; }
        DefaultValues = defaultValues;
        MissingValues = missingValues ?? new SqlBoundExpression?[table.Columns.Count];
        Domains = domains ?? [];
        foreach (var value in MissingValues) { ReadsJsonPath |= SqlJsonUsage.Contains(value, defaultCollation, default); }
        WritesJsonPath = ReadsJsonPath;
        foreach (var value in DefaultValues) { DefaultsUseJsonPath |= SqlJsonUsage.Contains(value, defaultCollation, default); }
        foreach (var check in checks) { WritesJsonPath |= SqlJsonUsage.Contains(check.Bound, defaultCollation, default); }
        foreach (var domain in Domains) { WritesJsonPath |= SqlJsonUsage.Contains(domain.Check, defaultCollation, default); }
        int references = 0;
        foreach (var constraint in table.Constraints) { if (constraint.Kind == SqlCatalogConstraintKind.Reference) { references++; } }
        if (references > 0)
        {
            // Own FK lists already live as long as this version. Keep their bindings compact;
            // allocating a weak-table bucket array per table inflated schema-only engine Build.
            _foreignKeys = new (IReadOnlyList<string>, SqlBoundConstraintKey)[references];
            int index = 0;
            foreach (var constraint in table.Constraints)
            {
                if (constraint.Kind == SqlCatalogConstraintKind.Reference) { _foreignKeys[index++] = (constraint.Columns, BindKey(constraint.Columns)); }
            }
        }
        if (Domains.Count > 0)
        {
            _domainsByOrdinal = new SqlBoundDomain?[table.Columns.Count];
            foreach (var domain in Domains)
            {
                _domainsByOrdinal[domain.Ordinal] = domain;
            }
        }
    }

    /// <summary>
    /// Gets the bound domains of the columns that have one, in column order (phase E3): what a write
    /// that assigns such a column evaluates. Empty for a table without a column of a domain.
    /// </summary>
    internal IReadOnlyList<SqlBoundDomain> Domains { get; }

    /// <summary>Finds the bound domain of a column.</summary>
    /// <param name="ordinal">The column's ordinal.</param>
    /// <returns>The bound domain, or null when the column is of a built-in type.</returns>
    internal SqlBoundDomain? DomainAt(int ordinal) => _domainsByOrdinal?[ordinal];

    /// <summary>Gets the table version the expressions were bound against.</summary>
    internal SqlCatalogTable Table { get; }

    internal int KeyBindCount => Volatile.Read(ref _keyBindCount);

    // Incoming constraints belong to the child version. Weak keys keep a long-lived parent
    // version from retaining every child definition ever replaced by DDL. A parent version
    // change gets an independent cache even when the child's constraint instance is unchanged.
    internal SqlBoundConstraintKey GetKey(IReadOnlyList<string> columns)
    {
        if (_foreignKeys is not null)
        {
            foreach (var (ownColumns, key) in _foreignKeys) { if (ReferenceEquals(ownColumns, columns)) { return key; } }
        }
        var keys = Volatile.Read(ref _keys);
        if (keys is not null && keys.TryGetValue(columns, out var cached)) { return cached; }
        lock (_keySync)
        {
            keys = _keys ??= new();
            if (keys.TryGetValue(columns, out cached)) { return cached; }
            var bound = BindKey(columns);
            keys.Add(columns, bound);
            return bound;
        }
    }

    private SqlBoundConstraintKey BindKey(IReadOnlyList<string> columns)
    {
        var ordinals = new int[columns.Count];
        var collations = new Collation[columns.Count];
        for (int key = 0; key < columns.Count; key++)
        {
            int ordinal = SqlPlanExecutor.FindColumnOrdinal(Table, columns[key]);
            ordinals[key] = ordinal;
            collations[key] = Table.Columns[ordinal].Collation ?? _defaultCollation;
        }
        Interlocked.Increment(ref _keyBindCount);
        return new SqlBoundConstraintKey(Table, ordinals, collations);
    }

    /// <summary>Gets the bound CHECK constraints, in declaration order.</summary>
    internal IReadOnlyList<SqlBoundCheck> Checks => _checks;

    internal bool ChecksContainLike { get; }

    // Usage belongs to this immutable table version. DROP/replacement cannot leave
    // a database-wide sticky bit, and reads do not inherit write-only CHECK work.
    internal bool ReadsJsonPath { get; }
    internal bool WritesJsonPath { get; }
    internal bool DefaultsUseJsonPath { get; }

    /// <summary>
    /// Gets each column's DEFAULT by ordinal, bound once
    /// (<see cref="SqlPlanExecutor.BindDefault"/>): a <see cref="SqlBoundConstant"/> holding the
    /// literal already converted to the column's type, or a <see cref="SqlBoundFailure"/> raising the
    /// conversion's failure each time the default is used; null where the column declares none.
    /// </summary>
    internal IReadOnlyList<SqlBoundExpression?> DefaultValues { get; }

    internal IReadOnlyList<SqlBoundExpression?> MissingValues { get; }

    /// <summary>Finds the bound form of one of this version's CHECK constraints.</summary>
    /// <param name="constraint">A CHECK constraint of <see cref="Table"/>.</param>
    /// <returns>The bound constraint.</returns>
    /// <exception cref="InvalidOperationException">The constraint does not belong to this table version.</exception>
    internal SqlBoundCheck GetCheck(SqlCatalogConstraint constraint)
    {
        foreach (var check in _checks)
        {
            if (ReferenceEquals(check.Constraint, constraint))
            {
                return check;
            }
        }

        throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlBoundTableGetCheckFailure, constraint.Name, Table.Schema, Table.Name));
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundTable value)
        {
            _ = value;
        }
    }
}
