using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Graph value equality, independent of materialization and CLR numeric tags.</summary>
internal sealed class GraphValueComparer : IEqualityComparer<object?>
{
    internal static readonly GraphValueComparer Instance = new();

    private GraphValueComparer() { }

    public new bool Equals(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) { return true; }
        if (left is null || right is null) { return false; }
        if (IsNumber(left) && IsNumber(right)) { return CompareNumbers(left, right) == 0; }
        return (left, right) switch
        {
            (GraphNode x, GraphNode y) => x.Id == y.Id,
            (GraphRelationship x, GraphRelationship y) => x.Id == y.Id,
            (GraphPath x, GraphPath y) => PathsEqual(x, y),
            (string x, string y) => StringComparer.Ordinal.Equals(x, y),
            _ => left.Equals(right),
        };
    }

    public int GetHashCode(object? value)
    {
        if (value is null) { return 0; }
        if (TryNumber(value, out var number)) { return number.GetHashCode(); }
        if (value is float or double) { return Convert.ToDouble(value, CultureInfo.InvariantCulture).GetHashCode(); }
        switch (value)
        {
            case GraphNode node: return HashCode.Combine(1, node.Id);
            case GraphRelationship relationship: return HashCode.Combine(2, relationship.Id);
            case GraphPath path:
                var hash = new HashCode();
                hash.Add(3);
                foreach (var node in path.Nodes) { hash.Add(node.Id); }
                foreach (var relationship in path.Relationships) { hash.Add(relationship.Id); }
                return hash.ToHashCode();
            case string text: return StringComparer.Ordinal.GetHashCode(text);
            default: return value.GetHashCode();
        }
    }

    internal static bool IsNumber(object value) => value is sbyte or short or int or long or byte or ushort or uint or ulong or float or double or decimal;

    internal static int CompareNumbers(object left, object right)
    {
        if (TryNumber(left, out var a) && TryNumber(right, out var b))
        { return (a.Numerator * b.Denominator).CompareTo(b.Numerator * a.Denominator); }
        // Planning rejects non-finite literals and parameters. Keep internal comparisons and
        // DISTINCT consistent even for NaNs (including different payloads and floating tags).
        return Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));
    }

    private static bool PathsEqual(GraphPath left, GraphPath right)
    {
        if (left.Nodes.Count != right.Nodes.Count || left.Relationships.Count != right.Relationships.Count) { return false; }
        for (int i = 0; i < left.Nodes.Count; i++) { if (left.Nodes[i].Id != right.Nodes[i].Id) { return false; } }
        for (int i = 0; i < left.Relationships.Count; i++) { if (left.Relationships[i].Id != right.Relationships[i].Id) { return false; } }
        return true;
    }

    // Use reduced exact fractions so equality is transitive and hashing agrees across all
    // numeric tags. Converting every number to Double would collapse adjacent Int64 values
    // above 2^53; converting Double to Decimal would round away subnormal values.
    private static bool TryNumber(object value, out (BigInteger Numerator, BigInteger Denominator) number)
    {
        BigInteger numerator;
        BigInteger denominator = BigInteger.One;
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long:
                numerator = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
            case ulong integer:
                numerator = integer;
                break;
            case decimal exact:
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(exact, bits);
                numerator = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
                if (bits[3] < 0) { numerator = -numerator; }
                denominator = BigInteger.Pow(10, (bits[3] >> 16) & 0xff);
                break;
            case float or double:
                double floating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(floating)) { number = default; return false; }
                long raw = BitConverter.DoubleToInt64Bits(floating);
                int exponent = (int)((raw >> 52) & 0x7ff);
                numerator = raw & 0x000f_ffff_ffff_ffffL;
                if (exponent != 0) { numerator += BigInteger.One << 52; }
                int shift = exponent == 0 ? -1074 : exponent - 1075;
                if (shift < 0) { denominator <<= -shift; }
                else { numerator <<= shift; }
                if (raw < 0) { numerator = -numerator; }
                break;
            default:
                number = default;
                return false;
        }
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        number = (numerator / divisor, denominator / divisor);
        return true;
    }
}
