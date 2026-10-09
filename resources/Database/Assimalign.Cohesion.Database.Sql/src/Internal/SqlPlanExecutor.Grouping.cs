using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
    private async Task<QueryResult> ExecuteGroupAsync(SqlGroupPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await using var input = (QueryResultSet)await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = SqlExpressionEvaluator.ForExecution(_subqueryValues, cancellationToken);
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
        var groups = new Dictionary<object?[], SqlAggregateAccumulator[]>(new GroupKeyComparer(keyCollations));
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
                row[i] = source.GetValue(i);
            }
            var key = keys.Count == 0 ? [] : new object?[keys.Count];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = evaluator.Evaluate(keys[i].Value, row);
            }
            if (!groups.TryGetValue(key, out var states))
            {
                states = CreateStates();
                groups.Add(key, states);
            }
            for (int i = 0; i < states.Length; i++)
            {
                AddRow(states[i], aggregates[i], aggregateCollations[i], evaluator, row, cancellationToken);
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
                row[key.Length + i] = states[i].Finish().ToObject();
            }
            if (!evaluator.Matches(plan.Having, row))
            {
                continue;
            }
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                row[projectionStart + i] = NormalizeGroupValue(
                    evaluator.Evaluate(plan.Projections[i].Value!, row), plan.Projections[i].Type);
            }
            matches.Add(row);
        }
        if (plan.OrderBy.Count > 0)
        {
            // The ordering keys were bound over the grouped row, outputs included.
            matches = SortRows(matches, plan.OrderBy, evaluator);
        }
        var projected = matches.Select(row => row[projectionStart..]).ToList();
        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected, plan.Projections, evaluator);
        }
        IEnumerable<object?[]> window = projected;
        if (plan.Offset is long skip)
        {
            window = skip > int.MaxValue ? [] : window.Skip((int)skip);
        }
        if (plan.Limit is long take && take <= int.MaxValue)
        {
            window = window.Take((int)take);
        }
        var columns = plan.Projections.Select((projection, ordinal) => new QueryColumn
        {
            Name = projection.Name,
            Ordinal = ordinal,
            Type = projection.Type,
            IsNullable = projection.IsNullable,
        }).ToArray();
        return new SqlMaterializedResultSet(columns, window.ToList());

        // One accumulator per group, per aggregate call, per statement, owned here and never shared.
        SqlAggregateAccumulator[] CreateStates()
        {
            var states = new SqlAggregateAccumulator[aggregates.Count];
            for (int i = 0; i < states.Length; i++)
            {
                var aggregate = aggregates[i];
                states[i] = aggregate.Function.CreateAccumulator(
                    new SqlFunctionContext(aggregate.Database, aggregateCollations[i], cancellationToken));
            }

            return states;
        }
    }

    /// <summary>
    /// Adds one input row to one group's accumulator of one aggregate call: its arguments evaluated
    /// over the row, converted to the value ABI on the stack (four inline, a pooled buffer past
    /// four) and to their parameters' types. A strict aggregate's accumulator skips the row when an
    /// argument is NULL; <c>COUNT(*)</c> has no argument, so it counts every row.
    /// </summary>
    private static void AddRow(SqlAggregateAccumulator state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] row, CancellationToken cancellationToken)
    {
        var arguments = aggregate.Arguments;
        var context = new SqlFunctionContext(aggregate.Database, collation, cancellationToken);
        switch (arguments.Length)
        {
            case 0:
                state.AddResolved(new SqlArguments([], context));
                return;
            case 1:
                // The shape of every standard-library aggregate but COUNT(*): one value, no buffer.
                SqlValue value = default;
                EvaluateArguments(aggregate, evaluator, row, new Span<SqlValue>(ref value));
                state.AddResolved(new SqlArguments(new ReadOnlySpan<SqlValue>(in value), context));
                return;
            case <= SqlValueBuffer.Length:
                SqlValueBuffer buffer = default;
                Span<SqlValue> values = buffer[..arguments.Length];
                EvaluateArguments(aggregate, evaluator, row, values);
                state.AddResolved(new SqlArguments(values, context));
                return;
            default:
                AddRowPooled(state, aggregate, collation, evaluator, row, cancellationToken);
                return;
        }
    }

    private static void AddRowPooled(SqlAggregateAccumulator state, SqlGroupAggregate aggregate, Collation collation,
        SqlExpressionEvaluator evaluator, object?[] row, CancellationToken cancellationToken)
    {
        SqlValue[] rented = ArrayPool<SqlValue>.Shared.Rent(aggregate.Arguments.Length);
        try
        {
            Span<SqlValue> values = rented.AsSpan(0, aggregate.Arguments.Length);
            EvaluateArguments(aggregate, evaluator, row, values);
            state.AddResolved(new SqlArguments(values, new SqlFunctionContext(aggregate.Database, collation, cancellationToken)));
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
            var value = SqlValue.FromObject(evaluator.Evaluate(arguments[i], row));
            try
            {
                values[i] = targets is null || targets[i] == DatabaseType.Null
                    ? value
                    : SqlFunctionResolver.Coerce(value, targets[i], aggregate.Function, i);
            }
            catch (ArithmeticException exception)
            {
                // An integer that does not fit its parameter, coded as the evaluator codes one.
                throw SqlEvaluationException.FromArithmetic(exception);
            }
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
