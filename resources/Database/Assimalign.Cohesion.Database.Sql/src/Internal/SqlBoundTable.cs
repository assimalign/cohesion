using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One table version with its persisted expressions parsed and bound: the CHECK predicates in
/// declaration order and the value each column DEFAULT supplies. Built once per
/// <see cref="SqlCatalogTable"/> instance by <see cref="SqlBoundTableCache"/>, so the write and
/// read paths never parse catalog text or convert a DEFAULT again.
/// </summary>
internal sealed class SqlBoundTable
{
    private readonly IReadOnlyList<SqlBoundCheck> _checks;

    // The bound domain of each column by ordinal, null where the column has none; null when no column has one.
    private readonly SqlBoundDomain?[]? _domainsByOrdinal;

    /// <summary>Initializes a bound table version.</summary>
    /// <param name="table">The table version the expressions were bound against.</param>
    /// <param name="checks">The bound CHECK constraints, in declaration order.</param>
    /// <param name="defaultValues">Each column's bound DEFAULT by ordinal; null where the column declares none.</param>
    /// <param name="domains">The bound domains of the columns that have one, in column order; empty when none has.</param>
    internal SqlBoundTable(SqlCatalogTable table, IReadOnlyList<SqlBoundCheck> checks, IReadOnlyList<SqlBoundExpression?> defaultValues,
        IReadOnlyList<SqlBoundDomain>? domains = null)
    {
        Table = table;
        _checks = checks;
        DefaultValues = defaultValues;
        Domains = domains ?? [];
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

    /// <summary>Gets the bound CHECK constraints, in declaration order.</summary>
    internal IReadOnlyList<SqlBoundCheck> Checks => _checks;

    /// <summary>
    /// Gets each column's DEFAULT by ordinal, bound once
    /// (<see cref="SqlPlanExecutor.BindDefault"/>): a <see cref="SqlBoundConstant"/> holding the
    /// literal already converted to the column's type, or a <see cref="SqlBoundFailure"/> raising the
    /// conversion's failure each time the default is used; null where the column declares none.
    /// </summary>
    internal IReadOnlyList<SqlBoundExpression?> DefaultValues { get; }

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

        throw new InvalidOperationException($"CHECK constraint '{constraint.Name}' is not bound for table '{Table.Schema}.{Table.Name}'.");
    }
}
