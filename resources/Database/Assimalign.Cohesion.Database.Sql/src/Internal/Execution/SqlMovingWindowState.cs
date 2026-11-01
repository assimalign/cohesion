using System;
using System.Globalization;
using System.Numerics;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Inverse native transitions and a monotonic extremum queue for one moving interval.</summary>
[System.Diagnostics.DebuggerDisplay("SqlMovingWindowState")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlMovingWindowState
{
    internal static object CountedRow { get; } = true;

    private readonly string _name;
    private readonly Collation _collation;
    private readonly object?[] _values;
    private readonly int[] _queue;
    private readonly int[] _scales;
    private int _head;
    private int _length;
    private long _count;
    private long _trueCount;
    private BigInteger _sum;
    private BigInteger _squares;
    private long _nonfiniteCount;
    private bool _floating;

    internal SqlMovingWindowState(string name, Collation collation, object?[] values, Action<long> reserve, bool floatingStatistics)
    {
        _name = name;
        _collation = collation;
        _values = values;
        // Includes the object and bounded transition limbs: decimal scale 28 or binary
        // scale 1074, with Int32 partition length. Queue storage is admitted separately.
        reserve(floatingStatistics ? 2048 : 512);
        _scales = name is "SUM" or "AVG" ? new int[29] : [];
        if (name is "MIN" or "MAX")
        {
            reserve(24L + ((4L * values.Length + 7) & ~7L));
            _queue = new int[values.Length];
        }
        else { _queue = []; }
    }

    internal int First { get; set; }
    internal int Last { get; set; }

    internal void Add(int position) => Change(position, 1);
    internal void Remove(int position) => Change(position, -1);

    private void Change(int position, int direction)
    {
        var value = _values[position];
        if (value is null) { return; }
        _count += direction;
        if (_name == "COUNT") { return; }
        if (_name is "BOOL_AND" or "BOOL_OR" or "EVERY")
        { if ((bool)value) { _trueCount += direction; } return; }
        if (_queue.Length > 0)
        {
            if (direction < 0)
            {
                if (_length > 0 && _queue[_head] == position) { _head = (_head + 1) % _queue.Length; _length--; }
            }
            else
            {
                while (_length > 0 && Math.Sign(SqlValueComparer.Compare(value, _values[_queue[(_head + _length - 1) % _queue.Length]]!, _collation)) == (_name == "MAX" ? 1 : -1)) { _length--; }
                _queue[(_head + _length) % _queue.Length] = position;
                _length++;
            }
            return;
        }
        bool statistics = _name is not ("SUM" or "AVG");
        if (statistics && value is float or double)
        {
            _floating = true;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number)) { _nonfiniteCount += direction; return; }
            // Every finite double is an integer multiple of 2^-1074. Exact binary
            // transitions remain invertible even while infinity/NaN masks the result.
            long bits = BitConverter.DoubleToInt64Bits(number);
            int exponent = (int)((bits >> 52) & 0x7ff);
            ulong mantissa = (ulong)bits & 0x000fffffffffffffUL;
            BigInteger scaled = exponent == 0 ? mantissa : (BigInteger)(mantissa | 0x0010000000000000UL) << (exponent - 1);
            if (bits < 0) { scaled = -scaled; }
            _sum += direction * scaled;
            _squares += direction * scaled * scaled;
            return;
        }
        try
        {
            decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (_scales.Length > 0)
            {
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(number, bits);
                _scales[(bits[3] >> 16) & 0xff] += direction;
            }
            var scaled = SqlWindowRange.Scaled(number);
            _sum += direction * scaled;
            if (statistics) { _squares += direction * scaled * scaled; }
        }
        catch (OverflowException exception) { throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.StatisticsOutOfRange, exception); }
    }

    internal static object? Result(SqlMovingWindowState[] states)
    {
        var first = states[0];
        string name = first._name;
        long count = 0, trues = 0, nonfinite = 0;
        BigInteger sum = 0, squares = 0;
        object? extreme = null;
        int minimumScale = 0;
        foreach (var state in states)
        {
            count += state._count;
            trues += state._trueCount;
            sum += state._sum;
            squares += state._squares;
            nonfinite += state._nonfiniteCount;
            for (int scale = state._scales.Length - 1; scale > minimumScale; scale--)
            { if (state._scales[scale] > 0) { minimumScale = scale; break; } }
            if (state._length > 0)
            {
                var candidate = state._values[state._queue[state._head]];
                if (extreme is null || Math.Sign(SqlValueComparer.Compare(candidate!, extreme, first._collation)) == (name == "MAX" ? 1 : -1)) { extreme = candidate; }
            }
        }
        if (name == "COUNT") { return count; }
        if (count == 0) { return null; }
        if (name is "MIN" or "MAX") { return extreme; }
        if (name is "BOOL_AND" or "EVERY") { return trues == count; }
        if (name == "BOOL_OR") { return trues > 0; }
        var unit = BigInteger.Pow(10, 28);
        if (name is "SUM" or "AVG")
        {
            if (TryExactSum(sum, minimumScale, out decimal exactSum)) { return name == "AVG" ? exactSum / count : exactSum; }
            return DecimalResult(sum, unit * (name == "AVG" ? count : 1), false, tiesToEven: true);
        }
        bool sample = name is not ("VAR_POP" or "STDDEV_POP");
        bool root = name.StartsWith("STDDEV", StringComparison.Ordinal);
        if (sample && count == 1) { return null; }
        bool floating = false;
        foreach (var state in states) { floating |= state._floating; }
        if (floating)
        {
            if (nonfinite > 0) { return double.NaN; }
            double total = BinaryQuotient(sum, BigInteger.One, -1074);
            double variance = BinaryQuotient(count * squares - sum * sum, (BigInteger)count * (sample ? count - 1 : count), -2148);
            if (!double.IsFinite(total) || !double.IsFinite(variance))
            { throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.StatisticsOutOfRange); }
            return root ? Math.Sqrt(variance) : variance;
        }
        return DecimalResult(count * squares - sum * sum, (BigInteger)count * (sample ? count - 1 : count) * unit * unit, root, tiesToEven: false);
    }

    private static bool TryExactSum(BigInteger sum, int scale, out decimal result)
    {
        // Keep ordinary SUM/AVG decimal representation when the exact fixed-scale sum
        // fits. Wider sums still use one final rounding, including AVG(max + max).
        bool negative = sum.Sign < 0;
        var coefficient = BigInteger.DivRem(BigInteger.Abs(sum), BigInteger.Pow(10, 28 - scale), out var remainder);
        var maximum = (BigInteger.One << 96) - 1;
        if (!remainder.IsZero) { result = 0; return false; }
        while (coefficient > maximum && scale > 0)
        {
            coefficient = BigInteger.DivRem(coefficient, 10, out remainder);
            if (!remainder.IsZero) { result = 0; return false; }
            scale--;
        }
        if (coefficient > maximum) { result = 0; return false; }
        result = new decimal((int)(uint)(coefficient & uint.MaxValue), (int)(uint)((coefficient >> 32) & uint.MaxValue),
            (int)(uint)(coefficient >> 64), negative, (byte)scale);
        return true;
    }

    private static double BinaryQuotient(BigInteger numerator, BigInteger denominator, int exponent)
    {
        if (numerator.IsZero) { return 0; }
        bool negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);
        int shift = (int)Math.Max(0, numerator.GetBitLength() - 64);
        double result = Math.ScaleB((double)(numerator >> shift) / (double)denominator, shift + exponent);
        return negative ? -result : result;
    }

    private static decimal DecimalResult(BigInteger numerator, BigInteger denominator, bool root, bool tiesToEven)
    {
        bool negative = numerator.Sign < 0;
        numerator = BigInteger.Abs(numerator);
        var maximum = (BigInteger.One << 96) - 1;
        for (int scale = 28; scale >= 0; scale--)
        {
            var factor = BigInteger.Pow(10, scale);
            BigInteger coefficient;
            if (root)
            {
                var radicand = numerator * factor * factor;
                coefficient = SquareRoot(radicand / denominator);
                if ((radicand * 4).CompareTo(denominator * BigInteger.Pow(coefficient * 2 + 1, 2)) >= 0) { coefficient++; }
            }
            else
            {
                coefficient = BigInteger.DivRem(numerator * factor, denominator, out var remainder);
                int rounding = (remainder * 2).CompareTo(denominator);
                if (rounding > 0 || rounding == 0 && (!tiesToEven || !coefficient.IsEven)) { coefficient++; }
            }
            if (coefficient > maximum) { continue; }
            return new decimal((int)(uint)(coefficient & uint.MaxValue), (int)(uint)((coefficient >> 32) & uint.MaxValue),
                (int)(uint)(coefficient >> 64), negative, (byte)scale);
        }
        throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.StatisticsOutOfRange);
    }

    private static BigInteger SquareRoot(BigInteger value)
    {
        if (value.IsZero) { return value; }
        var result = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
        while (true)
        {
            var next = (result + value / result) / 2;
            if (next >= result) { return result; }
            result = next;
        }
    }

    private sealed class DebugView
    {
        public DebugView(SqlMovingWindowState value) { _ = value; }
    }
}
