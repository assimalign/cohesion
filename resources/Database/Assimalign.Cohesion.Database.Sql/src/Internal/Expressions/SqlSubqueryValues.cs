using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The values one statement's subqueries materialized, by slot (<see cref="SqlSubquerySlot.Id"/>):
/// what a bound scalar subquery, <c>EXISTS</c> or <c>IN (subquery)</c> reads. The executor fills a
/// slot before it runs the plan that reads it and clears it afterwards, so evaluation never opens a
/// query, session or read view (PostgreSQL keeps an init plan's output the same way, as a
/// <c>PARAM_EXEC</c> parameter the plan reads by number).
/// </summary>
/// <remarks>
/// One instance serves one statement, owned by its <see cref="SqlPlanExecutor"/>; the slots of every
/// nesting level share it, because the planner numbers a statement's subqueries from zero across all
/// levels. A statement runs on one logical thread at a time, so the store is not synchronized.
/// </remarks>
internal sealed class SqlSubqueryValues
{
    private object?[]?[] _values = new object?[]?[4];

    /// <summary>Records the values a subquery materialized.</summary>
    /// <param name="slot">The subquery's slot.</param>
    /// <param name="values">A scalar subquery's one value (NULL when it returned no row), an <c>EXISTS</c>'s Boolean, or every value of an <c>IN</c> subquery.</param>
    internal void Set(int slot, object?[] values)
    {
        if (slot >= _values.Length)
        {
            Array.Resize(ref _values, Math.Max(slot + 1, _values.Length * 2));
        }

        _values[slot] = values;
    }

    /// <summary>Forgets a slot's values once the plan that read them has run.</summary>
    /// <param name="slot">The subquery's slot.</param>
    internal void Clear(int slot)
    {
        if (slot < _values.Length)
        {
            _values[slot] = null;
        }
    }

    /// <summary>Gets a slot's values when its subquery has been materialized.</summary>
    /// <param name="slot">The subquery's slot.</param>
    /// <param name="values">The values.</param>
    /// <returns><see langword="true"/> when the slot holds values.</returns>
    internal bool TryGet(int slot, [NotNullWhen(true)] out object?[]? values)
    {
        values = slot < _values.Length ? _values[slot] : null;
        return values is not null;
    }

    /// <summary>
    /// Gets a slot's values. A slot read before its plan materialized it is an executor defect, not a
    /// user error.
    /// </summary>
    /// <param name="subqueries">The store, or null when the statement materialized no subquery.</param>
    /// <param name="slot">The subquery's slot.</param>
    /// <returns>The values.</returns>
    /// <exception cref="DatabaseException">The slot has not been materialized.</exception>
    internal static object?[] Get(SqlSubqueryValues? subqueries, int slot)
        => subqueries is not null && subqueries.TryGet(slot, out var values)
            ? values
            : throw new DatabaseException("A subquery must be materialized by its plan before scalar evaluation.");
}
