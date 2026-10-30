using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    // Optional test observations; ordinary planning retains no counter state.
    internal static Action<SqlCatalogIndex>? OrderedIndexInspected { get; set; }
    internal static Action<bool>? OrderedPrecheck { get; set; }
    internal static Action? OrderedCandidatesStarted { get; set; }

    private SqlAccessPath SelectOrderedAccessPath(SqlCatalogTable table, SqlSelectExpression select,
        IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> ordering,
        SqlBoundExpression? where, SqlExpressionEvaluator evaluator)
    {
        var original = SelectAccessPath(table, select.Where, OuterScope is null ? null : evaluator, out var predicates);
        if (original is SqlIndexSeekPath { Index.IsUnique: true } unique && unique.EqualityValues.Count == unique.Index.ColumnNames.Count)
        { return original; }
        if (ordering.Count == 0 || _subquerySlots.Count != 0 || OuterScope is not null || _queryScopes?.DeferQueryEvaluation == true ||
            ViewDefinitionBinding is not null || _inferringTableOutput && !_tableAsWithData || _commonTableState?.BindingUnused == true)
        { return original; }
        // Reject expressions even if constant folding happens to turn them into a column.
        // Aliases and ordinals may refer only to a pass-through output column.
        for (int i = 0; i < ordering.Count; i++)
        {
            if (ordering[i].Key is SqlBoundSlot slot)
            {
                int output = slot.Ordinal - table.Columns.Count;
                if ((uint)output >= (uint)projections.Count || projections[output].ColumnOrdinal is null) { return original; }
            }
            else if (ordering[i].Key is not SqlBoundColumn || UnwrapCollation(select.OrderBy[i].Expression) is not SqlColumnReferenceExpression)
            { return original; }
            int ordinal = OrderingOrdinal(ordering[i], table, projections);
            var metadata = table.Columns[ordinal];
            var collation = metadata.Collation ?? _catalog.DefaultCollation;
            if (!(IsRangeSargable(metadata.Type.Type) || metadata.Type.Type is DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Guid or DatabaseType.Binary) ||
                !ordering[i].Collation.IsFixed || ordering[i].Collation.Resolve(null) != collation ||
                metadata.Type.Type is DatabaseType.String or DatabaseType.Character && !collation.IsIndexBacked)
            { return original; }
        }

        var indexes = ViewImageState is { } image ? image.Snapshot.GetIndexes(table.ObjectId) : _catalog.GetPlanningIndexes(table.ObjectId);
        if (indexes.Count == 0) { return original; }
        bool hasNullEquality = CollectOrderedNullEqualities(where, null);
        OrderedPrecheck?.Invoke(false);
        bool possible = false;
        for (int indexOrdinal = 0; indexOrdinal < indexes.Count; indexOrdinal++)
        {
            var index = indexes[indexOrdinal];
            OrderedIndexInspected?.Invoke(index);
            if (MatchOrderedIndex(table, index, original, projections, ordering, where, predicates, hasNullEquality, out _, out _, out _)) { possible = true; break; }
        }
        OrderedPrecheck?.Invoke(true);
        if (!possible) { return original; }
        foreach (var projection in select.Columns)
        { if (ContainsVolatileCall(projection.Expression)) { return original; } }

        if (hasNullEquality) { CollectOrderedNullEqualities(where, predicates!); }
        OrderedCandidatesStarted?.Invoke();
        List<SqlIndexSeekPath>? candidates = null;
        for (int indexOrdinal = 0; indexOrdinal < indexes.Count; indexOrdinal++)
        {
            var index = indexes[indexOrdinal];
            if (!MatchOrderedIndex(table, index, original, projections, ordering, where, predicates, hasNullEquality, out var seek, out var proof, out bool reverse)) { continue; }
            seek = hasNullEquality ? TryBuildSeek(table, index, predicates!, out _, out _) : seek;
            seek ??= new SqlIndexSeekPath(index, [], null, null);
            if (candidates is null)
            {
                candidates = original is SqlIndexSeekPath existing ? new(existing.Cost?.Alternatives ?? [existing]) : [];
            }
            candidates.RemoveAll(path => ReferenceEquals(path.Index, index));
            candidates.Add(new SqlOrderedIndexPath(index, seek.EqualityValues, seek.Lower, seek.Upper, reverse, proof));
        }
        if (candidates is null) { return original; }
        var ordered = candidates.First(path => path is SqlOrderedIndexPath);
        ordered.Cost = new(candidates);
        return ordered;
    }

    private bool MatchOrderedIndex(SqlCatalogTable table, SqlCatalogIndex index, SqlAccessPath original,
        IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> ordering, SqlBoundExpression? where,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>? predicates, bool hasNullEquality,
        out SqlIndexSeekPath? seek, out string? proof, out bool reverse)
    {
        seek = null; proof = null; reverse = false;
        if (original is SqlIndexSeekPath chosen)
        {
            if (ReferenceEquals(chosen.Index, index)) { seek = chosen; }
            else if (chosen.Cost is { } cost)
            {
                for (int i = 0; i < cost.Alternatives.Count; i++)
                { if (ReferenceEquals(cost.Alternatives[i].Index, index)) { seek = cost.Alternatives[i]; break; } }
            }
        }
        // Ordinary access has already performed the same strong implication proof,
        // including for an unbounded partial path. Reuse that membership proof.
        if (index.Predicate is not null)
        {
            if (seek is not SqlPartialIndexSeekPath partial) { return false; }
            proof = partial.Proof;
        }
        int equalityPrefix = seek?.EqualityValues.Count ?? 0;
        if (hasNullEquality)
        {
            equalityPrefix = 0;
            foreach (string name in index.ColumnNames)
            {
                int ordinal = FindColumnOrdinal(table, name);
                bool fixedKey = OrderedNullEquality(where, ordinal);
                if (predicates is not null && predicates.TryGetValue(ordinal, out var values))
                { for (int i = 0; i < values.Count; i++) { fixedKey |= values[i].Op == SqlBinaryOperator.Equal; } }
                if (!fixedKey) { break; }
                equalityPrefix++;
            }
        }
        // An equality-bound leading component may be omitted from ORDER BY. The
        // remaining requested keys must be a contiguous prefix of the suffix.
        // PostgreSQL pathkeys.c:720-814 flips direction AND NULL placement together.
        for (int start = 0; start <= equalityPrefix && start + ordering.Count <= index.ColumnNames.Count; start++)
        {
            bool forward = true, backward = true;
            for (int i = 0; i < ordering.Count; i++)
            {
                var order = ordering[i];
                int ordinal = OrderingOrdinal(order, table, projections);
                if (!string.Equals(table.Columns[ordinal].Name, index.ColumnNames[start + i], StringComparison.OrdinalIgnoreCase))
                { forward = backward = false; break; }
                var keyOrder = index.KeyOrders[start + i];
                bool descending = ((int)keyOrder & 2) != 0, nullsFirst = ((int)keyOrder & 1) != 0;
                bool noNulls = start + i < equalityPrefix || !table.Columns[ordinal].IsNullable || SqlPredicateImplication.RejectsNull(where, ordinal) ||
                    index.Predicate is not null && SqlPredicateImplication.RejectsNull(
                        SqlPartialIndexPredicate.Bind(table, index, _catalog.DefaultCollation, _functions, _cancellationToken), ordinal);
                forward &= order.IsDescending == descending && (noNulls || order.NullsFirst == nullsFirst);
                backward &= order.IsDescending != descending && (noNulls || order.NullsFirst != nullsFirst);
            }
            if (forward || backward) { reverse = backward; return true; }
        }
        reverse = false;
        return false;
    }

    private static int OrderingOrdinal(SqlBoundOrdering order, SqlCatalogTable table, IReadOnlyList<SqlProjection> projections)
        => order.Key is SqlBoundColumn column ? column.Ordinal
            : projections[((SqlBoundSlot)order.Key).Ordinal - table.Columns.Count].ColumnOrdinal!.Value;

    private static bool OrderedNullEquality(SqlBoundExpression? expression, int ordinal)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            foreach (var term in logical.Operands) { if (OrderedNullEquality(term, ordinal)) { return true; } }
            return false;
        }
        return expression is SqlBoundIsNull { IsNegated: false, Operand: SqlBoundColumn column } && column.Ordinal == ordinal;
    }

    // IS NULL fixes a key just as equality does. Keep this admission local to the
    // new ordered path; ordinary SELECT/DML access selection retains its prior work.
    private static bool CollectOrderedNullEqualities(SqlBoundExpression? expression,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>? predicates)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            bool found = false;
            foreach (var term in logical.Operands) { found |= CollectOrderedNullEqualities(term, predicates); }
            return found;
        }
        if (expression is not SqlBoundIsNull { IsNegated: false, Operand: SqlBoundColumn column }) { return false; }
        if (predicates is not null)
        {
            if (!predicates.TryGetValue(column.Ordinal, out var values)) { predicates.Add(column.Ordinal, values = []); }
            values.Add((SqlBinaryOperator.Equal, null));
        }
        return true;
    }
}
