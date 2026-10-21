using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private async Task<DatabaseResult> ExecuteGroupAsync(SqlGroupPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await using var input = (DatabaseResultSet)await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var keys = plan.Keys;
        var aggregates = plan.Aggregates;
        var keyCollations = new Collation[keys.Count];
        for (int i = 0; i < keyCollations.Length; i++)
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
        if (keys.Count == 0)
        {
            // The implicit global group exists even on empty input.
            groups.Add([], CreateStates());
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
            if (!groups.TryGetValue(key, out var states))
            {
                states = CreateStates();
                groups.Add(key, states);
            }
            for (int i = 0; i < states.Length; i++)
            {
                foreach (var argument in aggregates[i].Arguments)
                {
                    await PrepareSubqueriesAsync(argument, row, statement, cancellationToken).ConfigureAwait(false);
                }
                foreach (var ordering in aggregates[i].OrderBy)
                {
                    await PrepareSubqueriesAsync(ordering.Key, row, statement, cancellationToken).ConfigureAwait(false);
                }
                var state = states[i];
                if (state.Inputs is null && state.Distinct is null)
                {
                    AddRow(state.Accumulator, aggregates[i], aggregateCollations[i], evaluator, row, cancellationToken, statement.TransactionStart);
                }
                else { AddAggregateInput(state, aggregates[i], aggregateCollations[i], evaluator, row, cancellationToken, statement.TransactionStart); }
            }
        }

        int projectionStart = keys.Count + aggregates.Count;
        var matches = new List<object?[]>();
        foreach (var (key, states) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new object?[projectionStart + plan.Projections.Count];
            key.CopyTo(row, 0);
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
                row[key.Length + i] = state.Accumulator.Finish(cancellationToken).ToObject();
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
            matches.Add(row);
        }
        if (_subqueryValues is { HasCorrelatedQueries: true } && plan.OrderBy.Count > 0)
        {
            var output = plan.Projections.Select((projection, i) => projection with
            {
                Value = new SqlBoundSlot(projectionStart + i), ColumnOrdinal = null,
            }).ToArray();
            return await MaterializeCorrelatedSelectAsync(matches, output, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, evaluator, statement, cancellationToken).ConfigureAwait(false);
        }
        if (plan.OrderBy.Count > 0)
        {
            // The ordering keys were bound over the grouped row, outputs included.
            matches = SortRows(matches, plan.OrderBy, evaluator, cancellationToken);
        }
        var projected = matches.Select(row => row[projectionStart..]).ToList();
        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected, plan.Projections, evaluator);
        }
        var columns = plan.Projections.Select((projection, ordinal) => new DatabaseColumn
        {
            Name = projection.Name,
            Ordinal = ordinal,
            Type = projection.Type,
            MaxLength = projection.MaxLength,
            IsNullable = projection.IsNullable,
        }).ToArray();
        return new SqlMaterializedResultSet(columns, ApplyRowWindow(projected, plan.Limit, plan.Offset));

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

    // DISTINCT belongs to the executor so an application's accumulator sees the same
    // coerced SQL equivalence classes as built-ins. Ordered calls retain only distinct
    // tuples when requested, choosing their first representative in stable sorted order.
    private sealed class GroupAggregateState
    {
        internal readonly SqlAggregateAccumulator Accumulator;
        internal readonly Dictionary<object?[], object?[]>? Distinct;
        internal readonly List<object?[]>? Inputs;
        internal readonly IReadOnlyList<SqlBoundOrdering> Ordering;
        internal long StringLength;
        internal long StringCount;

        internal GroupAggregateState(SqlAggregateAccumulator accumulator, SqlGroupAggregate aggregate, SqlSubqueryValues? subqueries)
        {
            Accumulator = accumulator;
            if (aggregate.Call.Quantifier == Language.SqlSetQuantifier.Distinct)
            {
                Distinct = new(new GroupKeyComparer(aggregate.ArgumentCollations.Select(collation => collation.Resolve(subqueries)).ToArray()));
            }
            Ordering = aggregate.OrderBy.Count == 0 ? [] : aggregate.OrderBy.Select((key, i) => key with { Key = new SqlBoundSlot(aggregate.Arguments.Length + i) }).ToArray();
            if (Ordering.Count > 0) { Inputs = new List<object?[]>(); }
        }
    }

    private static void AddAggregateInput(GroupAggregateState state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] source, CancellationToken cancellationToken, DateTimeOffset transactionTimestamp)
    {
        var values = new SqlValue[aggregate.Arguments.Length];
        EvaluateArguments(aggregate, evaluator, source, values);
        var tuple = values.Select(value => SqlBinaryValue.CopyShared(value.ToObject())).ToArray();
        var row = new object?[tuple.Length + aggregate.OrderBy.Count];
        tuple.CopyTo(row, 0);
        for (int i = 0; i < aggregate.OrderBy.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row[tuple.Length + i] = evaluator.Evaluate(aggregate.OrderBy[i].Key, source, copySharedBinary: true);
        }
        // Keys are input expressions even for rows the transition function skips.
        if (aggregate.Function.NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput && values.Any(value => value.IsNull) ||
            aggregate.Function is SqlStringAggregateFunction && values[0].IsNull) { return; }
        if (state.Distinct is not null)
        {
            if (state.Distinct.TryGetValue(tuple, out var previous))
            {
                // Same tuple, but possibly a different spelling under its collation. Keep
                // the representative occurring first under the independently collated keys.
                if (state.Inputs is not null && CompareAggregateInputs(row, previous, aggregate, evaluator, cancellationToken) < 0)
                {
                    row.CopyTo(previous, 0);
                }
                return;
            }
            state.Distinct.Add(tuple, row);
        }
        if (state.Inputs is null)
        {
            state.Accumulator.AddResolved(new SqlArguments(values, aggregate.Database, collation, cancellationToken, transactionTimestamp));
            return;
        }
        if (aggregate.Function is SqlStringAggregateFunction)
        {
            state.StringLength += ((string)tuple[0]!).Length + (state.StringCount++ == 0 || tuple.Length == 1 ? 0 : ((string?)tuple[1])?.Length ?? 0);
            // Check while buffering, before sorting or allocating a group's output builder.
            SqlBuiltinScalarFunction.CheckLength(state.StringLength);
        }
        state.Inputs.Add(row);
    }

    private static int CompareAggregateInputs(object?[] left, object?[] right, SqlGroupAggregate aggregate, SqlExpressionEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < aggregate.OrderBy.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = aggregate.OrderBy[i];
            int comparison = CompareOrderingValues(left[aggregate.Arguments.Length + i], right[aggregate.Arguments.Length + i],
                key.IsDescending ? -1 : 1, key.NullsFirst ? -1 : 1, key.Collation.Resolve(evaluator.SubqueryValues));
            if (comparison != 0) { return comparison; }
        }
        return 0;
    }

    private static void AddAggregateValues(SqlAggregateAccumulator accumulator, SqlGroupAggregate aggregate,
        Collation collation, object?[] row, CancellationToken cancellationToken, DateTimeOffset transactionTimestamp)
    {
        var values = new SqlValue[aggregate.Arguments.Length];
        for (int i = 0; i < values.Length; i++) { values[i] = SqlValue.FromObject(row[i]); }
        accumulator.AddResolved(new SqlArguments(values, aggregate.Database, collation, cancellationToken, transactionTimestamp));
    }

    /// <summary>
    /// Adds one input row to one group's accumulator of one aggregate call, the same way for every
    /// aggregate, a built-in's or an application's: its arguments evaluated over the row, once,
    /// converted to the value ABI on the stack and to their parameters' types. A strict aggregate's
    /// accumulator skips the row when an argument is NULL; <c>COUNT(*)</c> has no argument, so it
    /// counts every row.
    /// </summary>
    /// <remarks>
    /// The frame holds no value of the ABI: each shape adds through a frame of its own (one
    /// argument, the shape of every standard-library aggregate but <c>COUNT(*)</c>, through the
    /// accumulator's internal <c>AddResolved</c>, which converts it and makes the coded call; none;
    /// more), so the prologue of the call every row makes per aggregate clears no buffer of four
    /// values, and it is inlined into the row loop, which then holds only references for it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow(SqlAggregateAccumulator state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] row, CancellationToken cancellationToken, DateTimeOffset transactionTimestamp)
    {
        var arguments = aggregate.Arguments;
        switch (arguments.Length)
        {
            case 1:
                // A strict aggregate's NULL row is skipped before anything is converted.
                object? argument = evaluator.Evaluate(arguments[0], row);
                if (argument is not null || aggregate.Function.NullBehavior != SqlNullBehavior.ReturnsNullOnNullInput)
                {
                    // Converted, added and coded in one frame of the accumulator's; not evaluated again.
                    state.AddResolved(argument, aggregate.FirstTarget, aggregate.Database, collation, cancellationToken, transactionTimestamp);
                }

                return;
            case 0:
                // COUNT(*): no argument, so no NULL for the strict rule to skip.
                state.AddResolved(aggregate.Database, collation, cancellationToken, transactionTimestamp);
                return;
            case <= SqlValueBuffer.Length:
                AddRowBuffered(state, aggregate, collation, evaluator, row, cancellationToken, transactionTimestamp);
                return;
            default:
                AddRowPooled(state, aggregate, collation, evaluator, row, cancellationToken, transactionTimestamp);
                return;
        }
    }

    /// <summary>Adds a row of a call of two to four arguments, converted into an inline buffer.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddRowBuffered(SqlAggregateAccumulator state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] row, CancellationToken cancellationToken, DateTimeOffset transactionTimestamp)
    {
        SqlValueBuffer buffer = default;
        Span<SqlValue> values = buffer[..aggregate.Arguments.Length];
        EvaluateArguments(aggregate, evaluator, row, values);
        state.AddResolved(new SqlArguments(values, aggregate.Database, collation, cancellationToken, transactionTimestamp));
    }

    private static void AddRowPooled(SqlAggregateAccumulator state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] row, CancellationToken cancellationToken, DateTimeOffset transactionTimestamp)
    {
        SqlValue[] rented = ArrayPool<SqlValue>.Shared.Rent(aggregate.Arguments.Length);
        try
        {
            Span<SqlValue> values = rented.AsSpan(0, aggregate.Arguments.Length);
            EvaluateArguments(aggregate, evaluator, row, values);
            state.AddResolved(new SqlArguments(values, aggregate.Database, collation, cancellationToken, transactionTimestamp));
        }
        finally
        {
            ArrayPool<SqlValue>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void EvaluateArguments(SqlGroupAggregate aggregate, SqlExpressionEvaluator evaluator, object?[] row, Span<SqlValue> values)
    {
        var arguments = aggregate.Arguments;
        var targets = aggregate.Targets;
        for (int i = 0; i < values.Length; i++)
        {
            ref var value = ref values[i];
            value = SqlValue.FromObject(evaluator.Evaluate(arguments[i], row));
            if (targets is not null && targets[i] != DatabaseType.Null
                && (targets[i] != value.Type || targets[i] == DatabaseType.Character))
            {
                value = Coerce(aggregate, i, value, targets[i]);
            }
        }
    }

    /// <summary>Converts one argument to its parameter's type, coding an integer that does not fit as the evaluator codes one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SqlValue Coerce(SqlGroupAggregate aggregate, int index, in SqlValue value, DatabaseType target)
    {
        try
        {
            return SqlFunctionResolver.Coerce(value, target, aggregate.Function, index);
        }
        catch (ArithmeticException exception)
        {
            throw SqlEvaluationException.FromArithmetic(exception);
        }
    }

    /// <summary>Uses SQL comparison equality, with NULL keys in one group and binary values by content.</summary>
    private sealed class GroupKeyComparer : IEqualityComparer<object?[]>
    {
        private readonly IReadOnlyList<Collation> _collations;

        /// <summary>Initializes a new instance of the <see cref="GroupKeyComparer"/> class.</summary>
        /// <param name="collations">The collation for each group key position.</param>
        public GroupKeyComparer(IReadOnlyList<Collation> collations)
        {
            _collations = collations;
        }

        public bool Equals(object?[]? left, object?[]? right)
        {
            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] is null ? right[i] is not null : right[i] is null
                    || SqlValueComparer.Compare(left[i]!, right[i]!, _collations[i]) != 0)
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(object?[] values)
        {
            var hash = new HashCode();
            for (int i = 0; i < values.Length; i++)
            {
                hash.Add(SqlValueComparer.GetHashCode(values[i], _collations[i]));
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Alternative numeric CASE/COALESCE branches share the declared output type. A
    /// value that does not fit that type (an approximate branch beyond Decimal's range,
    /// for example) fails the statement as out of range rather than escaping raw.
    /// </summary>
    private static object? NormalizeGroupValue(object? value, DatabaseType type)
    {
        if (value is not (sbyte or short or int or long or float or double or decimal))
        {
            return value;
        }

        // A value already of the output type is returned as it is: converting it to its own type
        // yields the same value and would only box it again.
        if (type switch
        {
            DatabaseType.Int8 => value is sbyte,
            DatabaseType.Int16 => value is short,
            DatabaseType.Int32 => value is int,
            DatabaseType.Int64 => value is long,
            DatabaseType.Float32 => value is float,
            DatabaseType.Float64 => value is double,
            DatabaseType.Decimal => value is decimal,
            _ => true,
        })
        {
            return value;
        }

        try
        {
            return type switch
            {
                DatabaseType.Int8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                DatabaseType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                DatabaseType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                DatabaseType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                DatabaseType.Float32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                DatabaseType.Float64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                DatabaseType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ => value,
            };
        }
        catch (OverflowException exception)
        {
            throw SqlEvaluationException.NumericValueOutOfRange(
                $"{Convert.ToString(value, CultureInfo.InvariantCulture)} does not fit the {type} result type.", exception);
        }
    }
}
