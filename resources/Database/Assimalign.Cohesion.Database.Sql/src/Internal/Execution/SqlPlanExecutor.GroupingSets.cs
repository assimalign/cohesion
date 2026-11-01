using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Consumes a filtered relation, accumulates one state per group, then applies
    /// HAVING, ordering, projection and pagination to completed groups.
    /// </summary>
    private async Task<DatabaseResult> ExecuteGroupingSetsAsync(SqlGroupingSetsPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await using var input = (DatabaseResultSet)await (RecursiveState is null
            ? ExecuteAsync(plan.Input, statement, cancellationToken)
            : RecursiveGroupInputAsync(plan, statement, cancellationToken)).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var keys = plan.Keys;
        var aggregates = plan.Aggregates;
        var groupingSets = plan.GroupingSets;
        using var memory = new SqlDmlMemory(statement.Metrics, _definitions.StatementMemoryBudget);
        memory.Reserve(112L + 32L * (keys.Count + aggregates.Count) + 8L * (groupingSets?.Length ?? 1) +
            (groupingSets?.Sum(set => 24L + 4L * set.Length) ?? 0));
        var keyCollations = new Collation[keys.Count + (groupingSets is null ? 0 : 1)];
        for (int i = 0; i < keys.Count; i++)
        {
            keyCollations[i] = keys[i].Collation.Resolve(_subqueryValues);
        }

        // Each aggregate call's input collation, resolved once for the statement: its functions'
        // contexts carry it (MIN and MAX compare text with it).
        var aggregateCollations = new Collation[aggregates.Count];
        for (int i = 0; i < aggregateCollations.Length; i++)
        {
            aggregateCollations[i] = aggregates[i].Collation.Resolve(_subqueryValues);
        }
        var groups = new Dictionary<object?[], GroupAggregateState[]>(new GroupKeyComparer(keyCollations));
        long stateBytes = 128L + 8L * keyCollations.Length;
        foreach (var aggregate in aggregates)
        {
            // Includes each state's ordering records/slots and DISTINCT comparer vector,
            // including empty groups that never retain an input tuple.
            stateBytes += 320L + 128L * aggregate.OrderBy.Count + 32L * aggregate.ArgumentCollations.Count;
        }
        if (groupingSets is not null)
        {
            for (int set = 0; set < groupingSets.Length; set++)
            {
                if (groupingSets[set].Length != 0) { continue; }
                var key = new object?[keys.Count + 1];
                key[keys.Count] = set;
                AddGroup(key);
            }
        }
        else if (keys.Count == 0)
        {
            // The implicit global group exists even on empty input.
            AddGroup([]);
        }
        await foreach (var source in input.GetRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new object?[source.FieldCount];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = SqlCharacter.Input(source.GetValue(i), input.Columns[i].Type);
            }
            var key = keys.Count == 0 ? [] : new object?[keys.Count];
            for (int i = 0; i < key.Length; i++)
            {
                await PrepareSubqueriesAsync(keys[i].Value, row, statement, cancellationToken).ConfigureAwait(false);
                key[i] = evaluator.Evaluate(keys[i].Value, row);
            }
            int setCount = groupingSets?.Length ?? 1;
            var rowStates = new GroupAggregateState[setCount][];
            for (int set = 0; set < setCount; set++)
            {
                var groupKey = key;
                if (groupingSets is not null)
                {
                    groupKey = new object?[keys.Count + 1];
                    foreach (int ordinal in groupingSets[set]) { groupKey[ordinal] = key[ordinal]; }
                    groupKey[keys.Count] = set;
                }
                if (!groups.TryGetValue(groupKey, out var states)) { states = AddGroup(groupKey); }
                rowStates[set] = states;
            }
            for (int i = 0; i < aggregates.Count; i++)
            {
                var aggregate = aggregates[i];
                if (aggregate.Filter is not null)
                {
                    // PostgreSQL execExpr.c:3708-3740 evaluates a transition's FILTER
                    // and arguments once per input, before updating its grouping sets.
                    await PrepareSubqueriesAsync(aggregate.Filter, row, statement, cancellationToken).ConfigureAwait(false);
                    if (!evaluator.Matches(aggregate.Filter, row)) { continue; }
                }
                foreach (var argument in aggregate.Arguments)
                { await PrepareSubqueriesAsync(argument, row, statement, cancellationToken).ConfigureAwait(false); }
                foreach (var ordering in aggregate.OrderBy)
                { await PrepareSubqueriesAsync(ordering.Key, row, statement, cancellationToken).ConfigureAwait(false); }
                var values = new SqlValue[aggregate.Arguments.Length];
                EvaluateArguments(aggregate, evaluator, row, values);
                var tuple = values.Select(value => SqlBinaryValue.CopyShared(value.ToObject())).ToArray();
                var inputRow = new object?[tuple.Length + aggregate.OrderBy.Count];
                tuple.CopyTo(inputRow, 0);
                for (int order = 0; order < aggregate.OrderBy.Count; order++)
                { inputRow[tuple.Length + order] = evaluator.Evaluate(aggregate.OrderBy[order].Key, row, copySharedBinary: true); }
                if (aggregate.Function.NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput && values.Any(value => value.IsNull) ||
                    aggregate.Function is SqlStringAggregateFunction && values[0].IsNull) { continue; }
                for (int set = 0; set < setCount; set++)
                {
                    AddGroupingSetInput(rowStates[set][i], aggregate, aggregateCollations[i], evaluator, values,
                        tuple, inputRow, cancellationToken, statement.TransactionStart, memory);
                }
            }
        }

        // Keep sequence-only row/ordering state out of the ordinary async frame.
        if (SqlSequenceExecution.Current is not null)
        {
            return await ProjectSequenceGroupingSetsAsync(plan, groups, evaluator, aggregateCollations,
                statement, cancellationToken).ConfigureAwait(false);
        }

        int projectionStart = keys.Count + aggregates.Count + (plan.GroupingArguments?.Length ?? 0);
        var matches = new List<object?[]>();
        bool correlatedOrdering = _subqueryValues is { HasCorrelatedQueries: true } && plan.OrderBy.Count > 0;
        var collected = new SqlRowCollector(plan.OrderBy, _subqueryValues,
            plan.IsDistinct ? null : plan.Limit, plan.IsDistinct ? null : plan.Offset, cancellationToken, statement.Metrics);
        Collation[]? projectionCollations = null;
        foreach (var (key, states) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!correlatedOrdering && collected.IsComplete) { break; }
            var row = new object?[projectionStart + plan.Projections.Count];
            Array.Copy(key, row, keys.Count);
            for (int i = 0; i < states.Length; i++)
            {
                // Once per group, the implicit empty group included.
                var state = states[i];
                if (state.Inputs is not null)
                {
                    var ordered = SortRows(state.Inputs, state.Ordering, evaluator, cancellationToken);
                    foreach (var values in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddAggregateValues(state.Accumulator, aggregates[i], aggregateCollations[i], values, cancellationToken, statement.TransactionStart);
                    }
                }
                row[keys.Count + i] = state.Accumulator.Finish(cancellationToken).ToObject();
            }
            if (plan.GroupingArguments is { } groupingArguments)
            {
                int[]? included = groupingSets is null ? null : groupingSets[(int)key[keys.Count]!];
                for (int i = 0; i < groupingArguments.Length; i++)
                {
                    int mask = 0;
                    foreach (int argument in groupingArguments[i])
                    { mask = (mask << 1) | (included is null || Array.IndexOf(included, argument) >= 0 ? 0 : 1); }
                    row[keys.Count + aggregates.Count + i] = mask;
                }
            }
            await PrepareSubqueriesAsync(plan.Having, row, statement, cancellationToken).ConfigureAwait(false);
            if (!evaluator.Matches(plan.Having, row))
            {
                continue;
            }
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                await PrepareSubqueriesAsync(plan.Projections[i].Value, row, statement, cancellationToken).ConfigureAwait(false);
                row[projectionStart + i] = NormalizeGroupValue(
                    evaluator.Evaluate(plan.Projections[i].Value!, row, copySharedBinary: true), plan.Projections[i].Type);
            }
            if (correlatedOrdering) { matches.Add(row); }
            else
            {
                if (plan.IsDistinct)
                {
                    projectionCollations = CaptureProjectionCollations(plan.Projections, evaluator, projectionCollations, cancellationToken);
                }
                var orderingKeys = plan.OrderBy.Count == 0 ? Array.Empty<object?>() : new object?[plan.OrderBy.Count];
                for (int i = 0; i < orderingKeys.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    orderingKeys[i] = evaluator.Evaluate(plan.OrderBy[i].Key, row);
                }
                collected.Add(row[projectionStart..], orderingKeys);
            }
        }
        if (correlatedOrdering)
        {
            var output = plan.Projections.Select((projection, i) => projection with
            {
                Value = new SqlBoundSlot(projectionStart + i), ColumnOrdinal = null,
            }).ToArray();
            return await MaterializeCorrelatedSelectAsync(matches, output, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, evaluator, statement, cancellationToken).ConfigureAwait(false);
        }
        return FinishSelect(collected.Finish(), plan.Projections, plan.Limit, plan.Offset,
            plan.IsDistinct, evaluator, cancellationToken, projectionCollations);

        GroupAggregateState[] AddGroup(object?[] key)
        {
            memory?.Reserve(stateBytes);
            memory?.Retain(key);
            var states = CreateStates();
            groups.Add(key, states);
            return states;
        }

        // One accumulator per group, per aggregate call, per statement, owned here and never shared.
        GroupAggregateState[] CreateStates()
        {
            var states = new GroupAggregateState[aggregates.Count];
            for (int i = 0; i < states.Length; i++)
            {
                var aggregate = aggregates[i];
                states[i] = new GroupAggregateState(aggregate.Function.CreateAccumulator(
                    new SqlFunctionContext(aggregate.Database, aggregateCollations[i], cancellationToken, statement.TransactionStart)), aggregate, _subqueryValues);
            }

            return states;
        }
    }

    private async Task<DatabaseResult> ProjectSequenceGroupingSetsAsync(SqlGroupingSetsPlan plan,
        Dictionary<object?[], GroupAggregateState[]> groups, SqlExpressionEvaluator evaluator,
        Collation[] aggregateCollations, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var keys = plan.Keys;
        var aggregates = plan.Aggregates;
        int projectionStart = keys.Count + aggregates.Count + (plan.GroupingArguments?.Length ?? 0);
        var matches = new List<object?[]>();
        var sequenceOrdering = !plan.IsDistinct && HasSequenceProjection(plan.Projections)
            ? OrderingOutputSlots(plan.OrderBy, projectionStart) : null;
        bool deferredSequence = sequenceOrdering is not null && plan.OrderBy.Count > 0 && !OrderingUsesSequence(plan.Projections, sequenceOrdering);
        bool correlatedOrdering = _subqueryValues is { HasCorrelatedQueries: true } && plan.OrderBy.Count > 0;
        var collected = new SqlRowCollector(plan.OrderBy, _subqueryValues,
            plan.IsDistinct ? null : plan.Limit, plan.IsDistinct ? null : plan.Offset, cancellationToken, statement.Metrics);
        Collation[]? projectionCollations = null;
        foreach (var (key, states) in groups)
        {
            using var rowScope = SqlSequenceExecution.Current?.BeginRow();
            cancellationToken.ThrowIfCancellationRequested();
            if (!deferredSequence && !correlatedOrdering && collected.IsComplete) { break; }
            var row = new object?[projectionStart + plan.Projections.Count];
            Array.Copy(key, row, keys.Count);
            for (int i = 0; i < states.Length; i++)
            {
                // Once per group, the implicit empty group included.
                var state = states[i];
                if (state.Inputs is not null)
                {
                    var ordered = SortRows(state.Inputs, state.Ordering, evaluator, cancellationToken);
                    foreach (var values in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddAggregateValues(state.Accumulator, aggregates[i], aggregateCollations[i], values, cancellationToken, statement.TransactionStart);
                    }
                }
                row[keys.Count + i] = state.Accumulator.Finish(cancellationToken).ToObject();
            }
            if (plan.GroupingArguments is { } groupingArguments)
            {
                int[]? included = plan.GroupingSets is null ? null : plan.GroupingSets[(int)key[keys.Count]!];
                for (int i = 0; i < groupingArguments.Length; i++)
                {
                    int mask = 0;
                    foreach (int argument in groupingArguments[i])
                    { mask = (mask << 1) | (included is null || Array.IndexOf(included, argument) >= 0 ? 0 : 1); }
                    row[keys.Count + aggregates.Count + i] = mask;
                }
            }
            await PrepareSubqueriesAsync(plan.Having, row, statement, cancellationToken).ConfigureAwait(false);
            if (!evaluator.Matches(plan.Having, row))
            {
                continue;
            }
            if (deferredSequence) { matches.Add(row[..projectionStart]); continue; }
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                await PrepareSubqueriesAsync(plan.Projections[i].Value, row, statement, cancellationToken).ConfigureAwait(false);
                row[projectionStart + i] = NormalizeGroupValue(
                    evaluator.Evaluate(plan.Projections[i].Value!, row, copySharedBinary: true), plan.Projections[i].Type);
            }
            if (sequenceOrdering is not null && collected.SkipSequenceOffset()) { continue; }
            if (correlatedOrdering) { matches.Add(row); }
            else
            {
                if (plan.IsDistinct)
                {
                    projectionCollations = CaptureProjectionCollations(plan.Projections, evaluator, projectionCollations, cancellationToken);
                }
                var orderingKeys = plan.OrderBy.Count == 0 ? Array.Empty<object?>() : new object?[plan.OrderBy.Count];
                for (int i = 0; i < orderingKeys.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    orderingKeys[i] = evaluator.Evaluate(plan.OrderBy[i].Key, row);
                }
                collected.Add(row[projectionStart..], orderingKeys);
            }
        }
        if (deferredSequence)
        {
            return await MaterializeSequenceSelectAsync(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
                evaluator, statement, cancellationToken, null, sequenceOrdering!).ConfigureAwait(false);
        }
        if (correlatedOrdering)
        {
            var output = plan.Projections.Select((projection, i) => projection with
            {
                Value = new SqlBoundSlot(projectionStart + i), ColumnOrdinal = null,
            }).ToArray();
            return await MaterializeCorrelatedSelectAsync(matches, output, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, evaluator, statement, cancellationToken).ConfigureAwait(false);
        }
        return FinishSelect(collected.Finish(), plan.Projections, plan.Limit, plan.Offset,
            plan.IsDistinct, evaluator, cancellationToken, projectionCollations);
    }

    private static void AddGroupingSetInput(GroupAggregateState state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, SqlValue[] values, object?[] tuple, object?[] inputRow,
        CancellationToken cancellationToken, DateTimeOffset transactionTimestamp, SqlDmlMemory? memory)
    {
        object?[] retained = inputRow;
        if (state.Distinct is not null)
        {
            if (state.Distinct.TryGetValue(tuple, out var previous))
            {
                if (state.Inputs is not null && CompareAggregateInputs(inputRow, previous, aggregate, evaluator, cancellationToken) < 0)
                { inputRow.CopyTo(previous, 0); }
                return;
            }
            // Representatives are mutable; every set owns its own copy. The immutable
            // tuple may be shared, and the statement ledger charges that payload once.
            memory?.Reserve(96);
            retained = (object?[])inputRow.Clone();
            memory?.Retain(tuple);
            memory?.Retain(retained);
            state.Distinct.Add(tuple, retained);
        }
        if (state.Inputs is null)
        {
            if (memory is not null)
            {
                long bytes = tuple.Sum(SqlHashJoinTable.ValueBytes);
                if (aggregate.Function is SqlStringAggregateFunction) { memory.Reserve(bytes); }
                else if (bytes > state.StringLength) { memory.Reserve(bytes - state.StringLength); state.StringLength = bytes; }
            }
            state.Accumulator.AddResolved(new SqlArguments(values, aggregate.Database, collation, cancellationToken, transactionTimestamp));
            return;
        }
        if (state.Distinct is null)
        {
            memory?.Reserve(32);
            retained = (object?[])inputRow.Clone();
            memory?.Retain(retained);
        }
        if (aggregate.Function is SqlStringAggregateFunction)
        {
            memory?.Reserve(tuple.Sum(SqlHashJoinTable.ValueBytes));
            state.StringLength += ((string)tuple[0]!).Length + (state.StringCount++ == 0 || tuple.Length == 1 ? 0 : ((string?)tuple[1])?.Length ?? 0);
            SqlBuiltinScalarFunction.CheckLength(state.StringLength);
        }
        state.Inputs.Add(retained);
    }

}
