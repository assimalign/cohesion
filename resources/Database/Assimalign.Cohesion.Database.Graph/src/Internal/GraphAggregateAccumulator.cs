using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Bounded, statically typed aggregate state; numeric promotion considers every retained input.</summary>
internal sealed class GraphAggregateAccumulator
{
    private static readonly BigInteger _scale = BigInteger.Pow(10, 28);
    private static readonly BigInteger _decimalMax = (BigInteger.One << 96) - 1;
    private readonly GqlAggregateExpression _expression;
    private readonly HashSet<object?>? _distinct;
    private readonly List<object?>? _list;
    private long _count;
    private BigInteger _exactSum;
    private BigInteger _floatingDecimalSum;
    private double _floatingSum;
    private double _average;
    private bool _decimal;
    private bool _unsigned;
    private bool _floating;
    private bool _outsideDecimal;
    private object? _extreme;
    private object? _orderedExtreme;
    private long _listBytes = 5;
    private int _listValues = 1;

    internal GraphAggregateAccumulator(GqlAggregateExpression expression)
    {
        _expression = expression;
        _distinct = expression.Distinct ? new HashSet<object?>(GraphValueComparer.Instance) : null;
        _list = expression.Function == GqlAggregateFunction.CollectList ? [] : null;
    }

    internal void Add(object? value, CancellationToken token)
    {
        if (value is null || _distinct is not null && !_distinct.Add(value)) { return; }
        try
        {
            _count = checked(_count + 1);
            switch (_expression.Function)
            {
                case GqlAggregateFunction.Count: break;
                case GqlAggregateFunction.CollectList:
                    AddList(value, token);
                    break;
                case GqlAggregateFunction.Min or GqlAggregateFunction.Max:
                    var ordered = GraphOrderComparer.Prepare(value, token);
                    int comparison = _extreme is null ? 0 : GraphOrderComparer.Compare(ordered, _orderedExtreme, token);
                    if (_extreme is null || (_expression.Function == GqlAggregateFunction.Min ? comparison < 0 : comparison > 0))
                    { _extreme = value; _orderedExtreme = ordered; }
                    break;
                default:
                    AddNumber(value);
                    break;
            }
        }
        catch (ArithmeticException error)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticFailure, _expression.Location, error); }
        catch (DatabaseTypeException error)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeLimit, _expression.Location, error); }
    }

    internal object? Finish()
    {
        try
        {
            switch (_expression.Function)
            {
                case GqlAggregateFunction.Count: return _count;
                case GqlAggregateFunction.CollectList: return _list!.ToArray();
                case GqlAggregateFunction.Min or GqlAggregateFunction.Max: return _extreme;
            }
            bool average = _expression.Function == GqlAggregateFunction.Avg;
            if (average && _count == 0) { return null; }
            if (_decimal || !average && _unsigned)
            {
                if (_outsideDecimal) { throw new OverflowException(ErrorMessages.ArithmeticFailure); }
                return DecimalValue(_exactSum + _floatingDecimalSum, average ? _count : 1);
            }
            if (average) { return Finite(_average); }
            if (_floating) { return Finite((double)_exactSum / (double)_scale + _floatingSum); }
            return checked((long)(_exactSum / _scale));
        }
        catch (ArithmeticException error)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticFailure, _expression.Location, error); }
    }

    private void AddNumber(object value)
    {
        if (!GraphValueComparer.IsNumber(value))
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.NumericRequired, _expression.Location); }
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        // Neo4j AvgFunction.scala:69-70 uses incrementalAverage. Weighted terms avoid
        // overflowing the difference for opposite finite Double extremes.
        double difference = number - _average;
        _average = double.IsFinite(difference) ? _average + difference / _count
            : _average * ((_count - 1d) / _count) + number / _count;
        if (value is float or double)
        {
            _floating = true;
            _floatingSum += number;
            try { _floatingDecimalSum += Scaled(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); }
            catch (OverflowException) { _outsideDecimal = true; }
        }
        else
        {
            _decimal |= value is decimal;
            _unsigned |= value is ulong;
            _exactSum += Scaled(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
    }

    private static BigInteger Scaled(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger coefficient = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
        if (bits[3] < 0) { coefficient = -coefficient; }
        return coefficient * BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
    }

    // Keep the exact sum until final promotion, then round once to Decimal's available
    // scale (ties to even). AVG does not overflow simply because its sum exceeds Decimal.
    private static decimal DecimalValue(BigInteger numerator, long count)
    {
        bool negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);
        BigInteger divisor = count;
        for (int scale = 28; scale >= 0; scale--, divisor *= 10)
        {
            BigInteger coefficient = BigInteger.DivRem(numerator, divisor, out var remainder);
            int half = (remainder * 2).CompareTo(divisor);
            if (half > 0 || half == 0 && !coefficient.IsEven) { coefficient++; }
            if (coefficient > _decimalMax) { continue; }
            return new decimal((int)(uint)(coefficient & uint.MaxValue), (int)(uint)((coefficient >> 32) & uint.MaxValue),
                (int)(uint)((coefficient >> 64) & uint.MaxValue), negative, (byte)scale);
        }
        throw new OverflowException(ErrorMessages.ArithmeticFailure);
    }

    private void AddList(object value, CancellationToken token)
    {
        _listBytes += GraphValueCodec.Measure(value);
        var pending = new Stack<(object? Value, int Depth)>();
        pending.Push((value, 1));
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++_listValues > GraphValueCodec.MaxValueCount || item.Depth > GraphValueCodec.MaxDepth || _listBytes > GraphValueCodec.MaxPayloadLength)
            { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
            if (item.Value is object?[] list) { foreach (var child in list) { pending.Push((child, item.Depth + 1)); } }
            else if (item.Value is IReadOnlyDictionary<string, object?> map)
            { foreach (var child in map.Values) { pending.Push((child, item.Depth + 1)); } }
        }
        _list!.Add(value);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw new OverflowException(ErrorMessages.ArithmeticFailure);
}
