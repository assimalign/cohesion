using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlAccessPath SelectOrderedAccessPath(SqlCatalogTable table, SqlSelectExpression select,
        IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> ordering,
        SqlBoundExpression? where, SqlExpressionEvaluator evaluator)
    {
        var original = SelectAccessPath(table, select.Where, OuterScope is null ? null : evaluator);
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

        int[]? rented = null;
        int width = ordering.Count * 2 + table.Columns.Count;
        Span<int> components = ordering.Count <= 64 && table.Columns.Count <= 64 ? stackalloc int[width]
            : (rented = ArrayPool<int>.Shared.Rent(width));
        try
        {
            for (int i = 0; i < ordering.Count; i++)
            {
                components[i] = OrderingOrdinal(ordering[i], table, projections);
                components[ordering.Count + i] = (ordering[i].IsDescending ? 2 : 0) | (ordering[i].NullsFirst ? 1 : 0);
            }
            var ordinals = components[..ordering.Count];
            var orders = components.Slice(ordering.Count, ordering.Count);
            var fixedColumns = components.Slice(ordering.Count * 2, table.Columns.Count);
            fixedColumns.Clear();
            MarkOrderedEqualityColumns(where, fixedColumns);
            bool possible = false;
            if (ViewImageState is { } image)
            {
                foreach (var index in image.Snapshot.GetIndexes(table.ObjectId))
                { if (SqlCatalog.HasKeyOrder(index, table, ordinals, orders, fixedColumns)) { possible = true; break; } }
            }
            else { possible = _catalog.ContainsKeyOrder(table, ordinals, orders, fixedColumns); }
            if (!possible) { return original; }
        }
        finally { if (rented is not null) { ArrayPool<int>.Shared.Return(rented); } }
        foreach (var projection in select.Columns)
        { if (ContainsVolatileCall(projection.Expression)) { return original; } }

        List<SqlIndexSeekPath>? candidates = null;
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>? predicates = null;
        bool hasNullEquality = CollectOrderedNullEqualities(where, null);
        foreach (var index in ReadIndexes(table.ObjectId).OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? proof = null;
            if (index.Predicate is not null)
            {
                if (where is null || ProvePartialIndex(table, index, [where], 0) is not { } proven) { continue; }
                proof = proven;
            }
            // Reuse bound predicate values when ordinary access already considered this index.
            var seek = original is SqlIndexSeekPath chosen
                ? chosen.Cost?.Alternatives.FirstOrDefault(path => ReferenceEquals(path.Index, index)) ??
                    (ReferenceEquals(chosen.Index, index) ? chosen : null) : null;
            if (seek is null || hasNullEquality)
            {
                if (predicates is null)
                {
                    predicates = new();
                    if (select.Where is not null) { CollectSargablePredicates(table, select.Where, predicates, evaluator); }
                    if (hasNullEquality) { CollectOrderedNullEqualities(where, predicates); }
                }
                seek = TryBuildSeek(table, index, predicates, out _, out _) ?? new SqlIndexSeekPath(index, [], null, null);
            }
            if (!MatchesIndexOrder(index, seek.EqualityValues.Count, out bool reverse)) { continue; }
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

        bool MatchesIndexOrder(SqlCatalogIndex index, int equalityPrefix, out bool reverse)
        {
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
                    forward &= order.IsDescending == descending && order.NullsFirst == nullsFirst;
                    backward &= order.IsDescending != descending && order.NullsFirst != nullsFirst;
                }
                if (forward || backward) { reverse = backward; return true; }
            }
            reverse = false;
            return false;
        }
    }

    private static int OrderingOrdinal(SqlBoundOrdering order, SqlCatalogTable table, IReadOnlyList<SqlProjection> projections)
        => order.Key is SqlBoundColumn column ? column.Ordinal
            : projections[((SqlBoundSlot)order.Key).Ordinal - table.Columns.Count].ColumnOrdinal!.Value;

    private static void MarkOrderedEqualityColumns(SqlBoundExpression? expression, Span<int> columns)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            foreach (var term in logical.Operands) { MarkOrderedEqualityColumns(term, columns); }
        }
        else if (expression is SqlBoundIsNull { IsNegated: false, Operand: SqlBoundColumn nullable })
        { columns[nullable.Ordinal] = 1; }
        else if (expression is SqlBoundBinary { Operator: SqlBinaryOperator.Equal } binary)
        {
            if (binary.Left is SqlBoundColumn left && binary.Right is not SqlBoundColumn) { columns[left.Ordinal] = 1; }
            if (binary.Right is SqlBoundColumn right && binary.Left is not SqlBoundColumn) { columns[right.Ordinal] = 1; }
        }
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
