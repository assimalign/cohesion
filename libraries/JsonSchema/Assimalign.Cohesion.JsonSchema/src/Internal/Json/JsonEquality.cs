using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// JSON Schema instance equality (2020-12 Core §4.2.2): numbers by mathematical value, strings by
/// code units after unescaping, arrays element-wise in order, objects by member set regardless of
/// order. The hash agrees with that equality.
/// </summary>
internal static class JsonEquality
{
    public static bool AreEqual(JsonElement left, JsonElement right)
    {
        JsonValueKind kind = left.ValueKind;
        if (kind != right.ValueKind)
        {
            return false;
        }

        switch (kind)
        {
            case JsonValueKind.Number:
                return NumbersEqual(left, right);
            case JsonValueKind.String:
                return StringsEqual(left, right);
            case JsonValueKind.Array:
                return ArraysEqual(left, right);
            case JsonValueKind.Object:
                return ObjectsEqual(left, right);
            default:
                return true;
        }
    }

    public static int GetHashCode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return JsonNumber.FromElement(element).GetHashCode();
            case JsonValueKind.String:
                return StringComparer.Ordinal.GetHashCode(element.GetString()!);
            case JsonValueKind.Array:
                HashCode array = new();
                array.Add(JsonValueKind.Array);
                foreach (JsonElement item in element.EnumerateArray())
                {
                    array.Add(GetHashCode(item));
                }

                return array.ToHashCode();
            case JsonValueKind.Object:
                int members = (int)JsonValueKind.Object;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    // Order-independent: the sum of per-member hashes.
                    members += HashCode.Combine(StringComparer.Ordinal.GetHashCode(property.Name), GetHashCode(property.Value));
                }

                return members;
            default:
                return (int)element.ValueKind;
        }
    }

    public static bool StringsEqual(JsonElement left, JsonElement right)
    {
        ReadOnlySpan<byte> l = JsonMarshal.GetRawUtf8Value(left);
        ReadOnlySpan<byte> r = JsonMarshal.GetRawUtf8Value(right);
        if (l.IndexOf((byte)'\\') < 0 && r.IndexOf((byte)'\\') < 0)
        {
            return l.SequenceEqual(r);
        }

        return left.ValueEquals(right.GetString());
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        ReadOnlySpan<byte> l = JsonMarshal.GetRawUtf8Value(left);
        ReadOnlySpan<byte> r = JsonMarshal.GetRawUtf8Value(right);
        return l.SequenceEqual(r) || JsonNumber.FromElement(left).Equals(JsonNumber.FromElement(right));
    }

    private static bool ArraysEqual(JsonElement left, JsonElement right)
    {
        if (left.GetArrayLength() != right.GetArrayLength())
        {
            return false;
        }

        JsonElement.ArrayEnumerator l = left.EnumerateArray();
        JsonElement.ArrayEnumerator r = right.EnumerateArray();
        while (l.MoveNext() && r.MoveNext())
        {
            if (!AreEqual(l.Current, r.Current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        int count = 0;
        foreach (JsonProperty property in left.EnumerateObject())
        {
            if (!right.TryGetProperty(property.Name, out JsonElement other) || !AreEqual(property.Value, other))
            {
                return false;
            }

            count++;
        }

        foreach (JsonProperty _ in right.EnumerateObject())
        {
            count--;
        }

        return count == 0;
    }

    /// <summary>
    /// An <see cref="IEqualityComparer{T}"/> over the same equality, for hash-based duplicate
    /// detection.
    /// </summary>
    public sealed class Comparer : IEqualityComparer<JsonElement>
    {
        public static readonly Comparer Instance = new();

        private Comparer()
        {
        }

        public bool Equals(JsonElement x, JsonElement y) => AreEqual(x, y);

        public int GetHashCode(JsonElement obj) => JsonEquality.GetHashCode(obj);
    }
}
