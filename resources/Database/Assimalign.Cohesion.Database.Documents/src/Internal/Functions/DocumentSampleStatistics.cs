using System;
using System.Globalization;
using System.Numerics;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentSampleStatistics")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentSampleStatistics.DebugView))]
internal sealed class DocumentSampleStatistics
{
    private static readonly BigInteger[] _powers = CreatePowers();
    private static readonly BigInteger _scaleSquared = _powers[28] * _powers[28];
    private static readonly BigInteger _maximum = (BigInteger.One << 96) - BigInteger.One;
    private long _count;
    private BigInteger _sum;
    private BigInteger _squares;

    internal DocumentSampleStatistics() { }

    internal void Add(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        coefficient *= _powers[28 - ((bits[3] >> 16) & 255)];
        if (bits[3] < 0) { coefficient = -coefficient; }
        _count = checked(_count + 1);
        _sum += coefficient;
        _squares += coefficient * coefficient;
    }

    internal decimal Finish(bool standardDeviation)
    {
        if (_count == 1) { return 0m; }
        var count = new BigInteger(_count);
        // Every decimal is an integer at scale 28. These exact moments cannot lose
        // small differences to a large common offset, overflow or underflow. Even at
        // Int64.MaxValue inputs the largest intermediate is bounded by 604 bits.
        BigInteger numerator = count * _squares - _sum * _sum;
        if (numerator.IsZero) { return 0m; }
        BigInteger denominator = count * (_count - 1) * _scaleSquared;
        bool fits = TryDecimalRatio(numerator, denominator, out decimal variance, out bool exact);
        if (!standardDeviation)
        {
            if (!fits) { throw new OverflowException(); }
            return variance;
        }
        // Keep SQRT's decimal-to-double convention for exact decimal variance. A
        // rounded or out-of-domain intermediate must not distort a representable root.
        if (numerator > _maximum * _maximum * denominator) { throw new OverflowException(); }
        double squared = fits && exact ? (double)variance : (double)numerator / (double)denominator;
        if (decimal.TryParse(Math.Sqrt(squared).ToString("R", CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out decimal root)) { return root; }

        // A floating root can round beyond MaxValue even though the exact root fits.
        // Here the root is at decimal's upper boundary, where its spacing is one.
        // Locate the integral root and round the exact rational at its midpoint.
        BigInteger lower = BigInteger.Zero, upper = _maximum + 1;
        while (upper - lower > 1)
        {
            BigInteger middle = (lower + upper) >> 1;
            if (middle * middle * denominator <= numerator) { lower = middle; }
            else { upper = middle; }
        }
        BigInteger twiceMidpoint = (lower << 1) + 1;
        int rounding = (numerator << 2).CompareTo(twiceMidpoint * twiceMidpoint * denominator);
        if (rounding > 0 || rounding == 0 && !lower.IsEven) { lower++; }
        return (decimal)lower;
    }

    private static bool TryDecimalRatio(BigInteger numerator, BigInteger denominator, out decimal value, out bool exact)
    {
        for (int scale = 28; scale >= 0; scale--)
        {
            BigInteger coefficient = BigInteger.DivRem(numerator * _powers[scale], denominator, out var remainder);
            int midpoint = (remainder << 1).CompareTo(denominator);
            if (midpoint > 0 || midpoint == 0 && !coefficient.IsEven) { coefficient++; }
            if (coefficient > _maximum) { continue; }
            exact = remainder.IsZero;
            value = new decimal(
                unchecked((int)(uint)(coefficient & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)), false, (byte)scale);
            return true;
        }
        exact = false;
        value = default;
        return false;
    }

    private static BigInteger[] CreatePowers()
    {
        var powers = new BigInteger[29];
        powers[0] = BigInteger.One;
        for (int index = 1; index < powers.Length; index++) { powers[index] = powers[index - 1] * 10; }
        return powers;
    }

    private sealed class DebugView
    {
        public DebugView(DocumentSampleStatistics value) { _ = value; }
    }
}
