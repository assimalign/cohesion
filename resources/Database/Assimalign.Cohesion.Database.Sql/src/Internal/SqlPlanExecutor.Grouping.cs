using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
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
        var sourceEvaluator = new SqlExpressionEvaluator(plan.SourceColumns, _parameters, plan.Bindings,
            defaultCollation: _catalog.DefaultCollation, subqueryValues: _subqueryValues);
        var groups = new Dictionary<object?[], AggregateState[]>(new GroupKeyComparer(
            plan.Keys.Select(expression => sourceEvaluator.ResolveCollation(expression)).ToArray()));
        if (plan.Keys.Count == 0)
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
            var key = plan.Keys.Select(expression => sourceEvaluator.Evaluate(expression, row)).ToArray();
            if (!groups.TryGetValue(key, out var states))
            {
                states = CreateStates();
                groups.Add(key, states);
            }
            for (int i = 0; i < states.Length; i++)
            {
                var argument = plan.Aggregates[i].Arguments[0];
                states[i].Add(argument is SqlStarExpression ? 1L : sourceEvaluator.Evaluate(argument, row));
            }
        }

        int projectionStart = plan.Keys.Count + plan.Aggregates.Count;
        var aliasSources = plan.ValueOrdinals.Where(pair => pair.Value >= projectionStart)
            .ToDictionary(pair => pair.Key, pair => plan.Projections[pair.Value - projectionStart].Expression!);
        var evaluator = new SqlExpressionEvaluator(plan.SourceColumns, _parameters, plan.Bindings, plan.ValueOrdinals,
            _catalog.DefaultCollation, aliasSources, _subqueryValues);
        var matches = new List<object?[]>();
        foreach (var (key, states) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new object?[projectionStart + plan.Projections.Count];
            key.CopyTo(row, 0);
            for (int i = 0; i < states.Length; i++)
            {
                row[key.Length + i] = states[i].Finish();
            }
            if (!evaluator.Matches(plan.Having, row))
            {
                continue;
            }
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                row[projectionStart + i] = NormalizeGroupValue(
                    evaluator.Evaluate(plan.Projections[i].Expression!, row), plan.Projections[i].Type);
            }
            matches.Add(row);
        }
        if (plan.OrderBy.Count > 0)
        {
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
            IsNullable = projection.Expression is not SqlFunctionCallExpression call
                || !call.FunctionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase),
        }).ToArray();
        return new SqlMaterializedResultSet(columns, window.ToList());

        AggregateState[] CreateStates() => plan.Aggregates.Select(call => new AggregateState(call.FunctionName,
            sourceEvaluator.ResolveCollation(call.Arguments[0]))).ToArray();
    }

    /// <summary>
    /// All five aggregates skip NULL operands. COUNT(*) supplies a non-null
    /// sentinel for each row. SUM/AVG accumulate decimal; MIN/MAX retain values.
    /// </summary>
    private sealed class AggregateState(string function, Collation collation)
    {
        private readonly string _function = function.ToUpperInvariant();
        private long _count;
        private decimal _sum;
        private object? _extreme;

        internal void Add(object? value)
        {
            if (value is null)
            {
                return;
            }
            try
            {
                _count = checked(_count + 1);
                switch (_function)
                {
                    case "SUM":
                    case "AVG":
                        if (value is not (sbyte or short or int or long or float or double or decimal))
                        {
                            throw new DatabaseException($"{_function} requires a numeric argument.");
                        }
                        _sum = checked(_sum + Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                        break;
                    case "MIN":
                        if (_extreme is null || CompareGroupValues(value, _extreme, collation) < 0)
                        {
                            _extreme = value;
                        }
                        break;
                    case "MAX":
                        if (_extreme is null || CompareGroupValues(value, _extreme, collation) > 0)
                        {
                            _extreme = value;
                        }
                        break;
                }
            }
            catch (OverflowException exception)
            {
                throw new DatabaseException($"{_function} overflow: the aggregate cannot be represented as a decimal or count.", exception);
            }
        }

        /// <summary>Decimal division preserves fractional averages, rounding to even when necessary.</summary>
        internal object? Finish() => _function switch
        {
            "COUNT" => _count,
            _ when _count == 0 => null,
            "SUM" => _sum,
            "AVG" => _sum / _count,
            _ => _extreme,
        };
    }

    /// <summary>Uses SQL comparison equality, with NULL keys in one group and binary values by content.</summary>
    private sealed class GroupKeyComparer(IReadOnlyList<Collation> collations) : IEqualityComparer<object?[]>
    {
        public bool Equals(object?[]? left, object?[]? right)
        {
            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] is null ? right[i] is not null : right[i] is null
                    || CompareGroupValues(left[i]!, right[i]!, collations[i]) != 0)
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
                var value = values[i];
                switch (value)
                {
                    case sbyte or short or int or long or float or double or decimal:
                        hash.Add(GroupNumber(value));
                        break;
                    case byte[] bytes:
                        foreach (byte item in bytes)
                        {
                            hash.Add(item);
                        }
                        break;
                    case string text:
                        hash.Add(collations[i].GetHashCode(text));
                        break;
                    default:
                        hash.Add(value);
                        break;
                }
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>Compares aggregate/group values using the existing SQL value ordering.</summary>
    private static int CompareGroupValues(object left, object right, Collation? collation = null)
    {
        if (left is byte[] a && right is byte[] b)
        {
            return a.AsSpan().SequenceCompareTo(b);
        }
        if (left is sbyte or short or int or long or float or double or decimal
            && right is sbyte or short or int or long or float or double or decimal)
        {
            object x = GroupNumber(left), y = GroupNumber(right);
            return (x, y) switch
            {
                (decimal first, decimal second) => first.CompareTo(second),
                (double first, double second) => first.CompareTo(second),
                (double first, _) => first > 0 ? 1 : -1,
                (_, double second) => second > 0 ? -1 : 1,
                _ => throw new DatabaseException("Invalid numeric grouping key."),
            };
        }
        return SqlExpressionEvaluator.Compare(left, right, collation);
    }

    /// <summary>
    /// Keeps the evaluator's decimal numeric equality and a matching hash. An
    /// approximate value outside decimal range remains comparable for grouping
    /// and extrema; only SUM/AVG require it to fit in a decimal accumulator.
    /// </summary>
    private static object GroupNumber(object value)
    {
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException) when (value is float or double)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Alternative numeric CASE/COALESCE branches share the declared output type.</summary>
    private static object? NormalizeGroupValue(object? value, DatabaseType type)
        => value is not (sbyte or short or int or long or float or double or decimal) ? value : type switch
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
