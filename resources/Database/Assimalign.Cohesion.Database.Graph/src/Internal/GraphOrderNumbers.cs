using System;
using System.Globalization;
using System.Numerics;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Finite CLR graph numbers are N * 2^exponent / 5^scale. N has at most 96 bits and
// scale at most 28. Cross-multiplication therefore fits in 162 bits, even when the
// binary exponent is -1074. Compare exponents before aligning the three limbs.
// This retains the owner's exact mixed numeric order without BigInteger allocations.
internal static class GraphOrderNumbers
{
    internal static int Compare(object left, object right)
    {
        var a = Parts(left);
        var b = Parts(right);
        int order = a.Sign.CompareTo(b.Sign);
        if (order != 0 || a.Sign == 0) { return order; }
        var first = Multiply(a.Numerator, PowerOfFive(b.Scale));
        var second = Multiply(b.Numerator, PowerOfFive(a.Scale));
        int firstLength = first.BitLength, secondLength = second.BitLength;
        order = (firstLength + a.Exponent).CompareTo(secondLength + b.Exponent);
        if (order == 0)
        {
            first = first.Shift(192 - firstLength);
            second = second.Shift(192 - secondLength);
            order = first.High.CompareTo(second.High);
            if (order == 0) { order = first.Middle.CompareTo(second.Middle); }
            if (order == 0) { order = first.Low.CompareTo(second.Low); }
        }
        return a.Sign < 0 ? -Math.Sign(order) : order;
    }

    private static (UInt128 Numerator, int Exponent, int Scale, int Sign) Parts(object value)
    {
        if (value is decimal exact)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(exact, bits);
            UInt128 numerator = (UInt128)(uint)bits[0] | (UInt128)(uint)bits[1] << 32 | (UInt128)(uint)bits[2] << 64;
            int scale = (bits[3] >> 16) & 0xff;
            return (numerator, -scale, scale, numerator == 0 ? 0 : bits[3] < 0 ? -1 : 1);
        }
        if (value is float or double)
        {
            ulong raw = BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            int exponent = (int)((raw >> 52) & 0x7ff);
            UInt128 numerator = raw & 0x000f_ffff_ffff_ffffUL;
            if (exponent != 0) { numerator |= (UInt128)1 << 52; }
            return (numerator, exponent == 0 ? -1074 : exponent - 1075, 0, numerator == 0 ? 0 : (raw >> 63) == 0 ? 1 : -1);
        }
        if (value is ulong unsigned) { return (unsigned, 0, 0, unsigned == 0 ? 0 : 1); }
        long integer = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return (integer < 0 ? (UInt128)(-(Int128)integer) : (UInt128)integer, 0, 0, Math.Sign(integer));
    }

    private static UInt128 PowerOfFive(int scale)
    {
        UInt128 power = 1, factor = 5;
        while (scale != 0)
        {
            if ((scale & 1) != 0) { power *= factor; }
            factor *= factor;
            scale >>= 1;
        }
        return power;
    }

    private static Limbs Multiply(UInt128 left, UInt128 right)
    {
        ulong a = (ulong)left, b = (ulong)right;
        ulong highA = (ulong)(left >> 64), highB = (ulong)(right >> 64);
        UInt128 low = (UInt128)a * b;
        UInt128 middle = (UInt128)highA * b + (UInt128)a * highB + (low >> 64);
        return new Limbs((ulong)low, (ulong)middle, highA * highB + (ulong)(middle >> 64));
    }

    private readonly record struct Limbs(ulong Low, ulong Middle, ulong High)
    {
        internal int BitLength => High != 0 ? 192 - BitOperations.LeadingZeroCount(High)
            : Middle != 0 ? 128 - BitOperations.LeadingZeroCount(Middle) : 64 - BitOperations.LeadingZeroCount(Low);

        internal Limbs Shift(int count)
        {
            var original = this;
            int words = count / 64, bits = count % 64;
            return new Limbs(Limb(0), Limb(1), Limb(2));

            ulong Limb(int index)
            {
                ulong value = original.At(index - words) << bits;
                return bits == 0 ? value : value | original.At(index - words - 1) >> (64 - bits);
            }
        }

        private ulong At(int index) => index switch { 0 => Low, 1 => Middle, 2 => High, _ => 0 };
    }
}
