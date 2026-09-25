using System;
using System.Globalization;
using System.Numerics;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Defines the non-null SQL value order shared by predicates, sorting, grouping
/// and extrema. Numeric equality compares represented values without rounding.
/// </summary>
internal static class SqlValueComparer
{
    /// <summary>Compares two non-null values using the resolved string collation.</summary>
    internal static int Compare(object left, object right, Collation? collation = null)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            bool leftApproximate = left is float or double;
            bool rightApproximate = right is float or double;
            if (leftApproximate && rightApproximate)
            {
                double first = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                double second = Convert.ToDouble(right, CultureInfo.InvariantCulture);
                // SQL's extension gives all NaNs one equality class below -infinity.
                if (double.IsNaN(first))
                {
                    return double.IsNaN(second) ? 0 : -1;
                }
                if (double.IsNaN(second))
                {
                    return 1;
                }
                // IEEE signed zeros compare equal; infinities retain their order.
                return first.CompareTo(second);
            }
            if (leftApproximate)
            {
                return CompareApproximateToExact(Convert.ToDouble(left, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(right, CultureInfo.InvariantCulture));
            }
            if (rightApproximate)
            {
                return -CompareApproximateToExact(Convert.ToDouble(right, CultureInfo.InvariantCulture),
                    Convert.ToDecimal(left, CultureInfo.InvariantCulture));
            }
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }
        if (left is byte[] firstBytes && right is byte[] secondBytes)
        {
            return firstBytes.AsSpan().SequenceCompareTo(secondBytes);
        }
        if (left is string firstText && right is string secondText)
        {
            return (collation ?? Collation.Binary).Compare(firstText, secondText);
        }
        if (left.GetType() == right.GetType() && left is IComparable comparable)
        {
            return comparable.CompareTo(right);
        }
        throw new DatabaseException($"Cannot compare values of types {left.GetType().Name} and {right.GetType().Name}.");
    }

    /// <summary>
    /// Hashes the same equality classes as <see cref="Compare"/>. Finite numerics
    /// use reduced exact fractions, independent of runtime type and decimal scale.
    /// NaN payloads and signed zeros each share one hash.
    /// </summary>
    internal static int GetHashCode(object? value, Collation collation)
    {
        if (value is null)
        {
            return 0;
        }
        if (IsNumber(value))
        {
            (BigInteger Numerator, BigInteger Denominator) fraction;
            if (value is float or double)
            {
                double approximate = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(approximate))
                {
                    return approximate.GetHashCode();
                }
                fraction = BinaryFraction(approximate);
            }
            else
            {
                fraction = DecimalFraction(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            }
            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(fraction.Numerator), fraction.Denominator);
            return HashCode.Combine(fraction.Numerator / divisor, fraction.Denominator / divisor);
        }
        if (value is byte[] bytes)
        {
            var hash = new HashCode();
            foreach (byte item in bytes)
            {
                hash.Add(item);
            }
            return hash.ToHashCode();
        }
        return value is string text ? collation.GetHashCode(text) : value.GetHashCode();
    }

    /// <summary>Recognizes the signed integer, decimal and IEEE SQL numeric families.</summary>
    private static bool IsNumber(object value) => value is sbyte or short or int or long or float or double or decimal;

    /// <summary>
    /// Compares the exact binary significand/exponent with the decimal coefficient/scale.
    /// Neither operand is rounded to the other's format, preserving transitivity even
    /// between adjacent doubles, tiny subnormals and 96-bit decimal integers.
    /// </summary>
    private static int CompareApproximateToExact(double approximate, decimal exact)
    {
        if (double.IsNaN(approximate) || double.IsNegativeInfinity(approximate))
        {
            return -1;
        }
        if (double.IsPositiveInfinity(approximate))
        {
            return 1;
        }

        var binary = BinaryFraction(approximate);
        var dec = DecimalFraction(exact);
        return (binary.Numerator * dec.Denominator).CompareTo(dec.Numerator * binary.Denominator);
    }

    /// <summary>Extracts the exact rational value of a finite IEEE binary64 number.</summary>
    private static (BigInteger Numerator, BigInteger Denominator) BinaryFraction(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7ff);
        ulong significand = bits & 0x000fffffffffffffUL;
        if (exponent != 0)
        {
            significand |= 1UL << 52;
        }
        int power = exponent == 0 ? -1074 : exponent - 1075;
        BigInteger binaryCoefficient = significand;
        if ((bits >> 63) != 0)
        {
            binaryCoefficient = -binaryCoefficient;
        }

        return power >= 0 ? (binaryCoefficient << power, BigInteger.One)
            : (binaryCoefficient, BigInteger.One << -power);
    }

    /// <summary>Extracts a decimal's signed 96-bit coefficient and power-of-ten denominator.</summary>
    private static (BigInteger Numerator, BigInteger Denominator) DecimalFraction(decimal value)
    {
        Span<int> parts = stackalloc int[4];
        decimal.GetBits(value, parts);
        BigInteger decimalCoefficient = (BigInteger)(uint)parts[0]
            + ((BigInteger)(uint)parts[1] << 32) + ((BigInteger)(uint)parts[2] << 64);
        if (parts[3] < 0)
        {
            decimalCoefficient = -decimalCoefficient;
        }
        int scale = (parts[3] >> 16) & 0xff;
        return (decimalCoefficient, BigInteger.Pow(10, scale));
    }
}
