using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The values one query execution frame materialized, by slot (<see cref="SqlSubquerySlot.Id"/>):
/// what a bound scalar subquery, <c>EXISTS</c> or <c>IN (subquery)</c> reads. The executor fills a
/// slot before it runs the plan that reads it and clears it afterwards, so evaluation never opens a
/// query, session or read view (PostgreSQL keeps an init plan's output the same way, as a
/// <c>PARAM_EXEC</c> parameter the plan reads by number).
/// </summary>
/// <remarks>
/// Each nested query execution owns a frame linked to its enclosing frame. Correlated values and
/// prepared lazy-branch conditions expire when the frame advances to another row; independent
/// child values and derived rows are cached under the enclosing statement snapshot. Outer references
/// read the row retained by the named enclosing frame. Execution has one logical thread, so these
/// frame-local stores need no synchronization.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlSubqueryValues")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryValues.DebugView))]
internal class SqlSubqueryValues
{
    private object?[]?[] _values = new object?[]?[4];
    private readonly SqlSubqueryValues? _parent;
    private readonly int _scope;

    internal SqlSubqueryValues(SqlSubqueryValues? parent = null, int scope = -1,
        IReadOnlyList<SqlSubqueryBinding>? queries = null, bool lazy = false)
    {
        _parent = parent;
        _scope = scope;
        Queries = queries?.ToDictionary(query => query.Slot) ?? [];
        StatementValues = parent?.StatementValues ?? [];
        HasCorrelatedQueries = lazy || Queries.Values.Any(query => query.IsCorrelated);
        Evaluated = HasCorrelatedQueries ? new() : null;
    }

    internal Dictionary<int, SqlSubqueryBinding> Queries { get; }
    internal SqlSubqueryValues? Parent => _parent;
    internal Dictionary<int, object?[]> StatementValues { get; }
    internal bool HasCorrelatedQueries { get; }
    internal object?[]? Row { get; private set; }
    internal Dictionary<SqlBoundExpression, object?>? Evaluated { get; }

    // Only a frame registering queries needs a reservation. Ordinary per-invocation
    // evaluation frames keep their original layout and allocation.
    internal static SqlSubqueryValues WithMemory(SqlSubqueryValues? parent, int scope,
        IReadOnlyList<SqlSubqueryBinding> queries, bool lazy, SqlStatementMetrics metrics, long budget)
        => new BudgetedValues(parent, scope, queries, lazy, metrics, budget);

    internal SqlStatementMetrics? MemoryMetrics
    {
        get
        {
            for (var frame = this; frame is not null; frame = frame._parent)
            { if (frame is BudgetedValues budgeted) { return budgeted.Metrics; } }
            return null;
        }
    }

    internal void Admit(object?[] values, bool correlated)
    {
        if (this is not BudgetedValues budgeted) { return; }
        bool windowOwned = SqlWindowMemory.For(budgeted.Metrics) is not null;
        if (windowOwned && correlated) { return; }
        long bytes = SqlSelectMemory.VectorBytes(values);
        if (!correlated)
        {
            for (var frame = _parent; frame is not null; frame = frame._parent)
            { if (frame is BudgetedValues parentBudget) { budgeted = parentBudget; } }
        }
        if (windowOwned) { budgeted.PendingStatementBytes += bytes; return; }
        budgeted.Metrics.ReservePlainSelectMemory(bytes, budgeted.Budget);
        if (correlated) { budgeted.CorrelatedBytes += bytes; }
        else { budgeted.StatementBytes += bytes; }
    }

    internal long PendingStatementMemoryBytes
    {
        get
        {
            BudgetedValues? owner = null;
            for (var frame = this; frame is not null; frame = frame._parent)
            { if (frame is BudgetedValues budgeted) { owner = budgeted; } }
            return owner?.PendingStatementBytes ?? 0;
        }
    }

    internal void AdmitPendingStatementMemory(long previousPending)
    {
        BudgetedValues? owner = null;
        for (var frame = this; frame is not null; frame = frame._parent)
        { if (frame is BudgetedValues budgeted) { owner = budgeted; } }
        if (owner is null) { return; }
        // New independent vectors belong to the outermost registration frame.
        // An inner window transfers only its own additions; earlier vectors stay
        // on the still-live parent window ledger until that stage ends.
        long bytes = owner.PendingStatementBytes - previousPending;
        if (bytes == 0) { return; }
        owner.Metrics.ReservePlainSelectMemory(bytes, owner.Budget);
        owner.StatementBytes += bytes;
        owner.PendingStatementBytes = previousPending;
    }

    internal void ReleaseMemory()
    {
        if (this is not BudgetedValues budgeted) { return; }
        budgeted.Metrics.ReleasePlainSelectMemory(budgeted.StatementBytes + budgeted.CorrelatedBytes);
        budgeted.StatementBytes = 0; budgeted.CorrelatedBytes = 0; budgeted.PendingStatementBytes = 0;
    }

    private sealed class BudgetedValues : SqlSubqueryValues
    {
        internal readonly SqlStatementMetrics Metrics;
        internal readonly long Budget;
        internal long StatementBytes;
        internal long PendingStatementBytes;
        internal long CorrelatedBytes;

        internal BudgetedValues(SqlSubqueryValues? parent, int scope, IReadOnlyList<SqlSubqueryBinding> queries,
            bool lazy, SqlStatementMetrics metrics, long budget) : base(parent, scope, queries, lazy)
        { Metrics = metrics; Budget = budget; }
    }

    internal void BeginRow(object?[] row, SqlWindowMemory? memory = null)
    {
        if (ReferenceEquals(Row, row)) { return; }
        Row = row;
        Evaluated?.Clear();
        ClearCorrelated(memory);
    }

    internal void ClearCorrelated(SqlWindowMemory? memory)
    {
        if (this is BudgetedValues budgeted)
        { budgeted.Metrics.ReleasePlainSelectMemory(budgeted.CorrelatedBytes); budgeted.CorrelatedBytes = 0; }
        foreach (var query in Queries.Values)
        {
            if (!query.IsCorrelated) { continue; }
            if (memory is not null && TryGet(query.Slot, out var values)) { memory.ReleaseRow(values); }
            Clear(query.Slot);
        }
    }

    internal SqlSubqueryValues FindScope(int scope)
    {
        for (var frame = this; frame is not null; frame = frame._parent)
        {
            if (frame._scope == scope) { return frame; }
        }
        throw new DatabaseException(ErrorMessages.SubqueryFrameMissing);
    }

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
            : throw new DatabaseException(ErrorMessages.EvaluatorNotMaterializedSqlPlanning);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlSubqueryValues value)
        {
            _ = value;
        }
    }
}
