using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    // Couchbase value/object.go:770-799 and value/array.go:751-779:
    // a definite mismatch wins over nested NULL; otherwise NULL is not equality.
    // IN consumes only TRUE (expression/coll_in.go:103-115), as does simple CASE.
    private static object? Equal(object? left, object? right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
        if (left is null || right is null) { return null; }
        if (Kind(left) != Kind(right)) { return false; }
        if (left is not JsonElement a || right is not JsonElement b)
        {
            return left switch
            {
                string text => string.Equals(text, (string)right, StringComparison.Ordinal),
                decimal number => number == (decimal)right,
                bool boolean => boolean == (bool)right,
                _ => false,
            };
        }

        bool unknown = false;
        if (a.ValueKind == JsonValueKind.Array)
        {
            if (a.GetArrayLength() != b.GetArrayLength()) { return false; }
            for (int i = 0; i < a.GetArrayLength(); i++)
            {
                var equal = Equal(a[i], b[i]);
                if (equal is false) { return false; }
                if (equal is null) { unknown = true; }
            }
        }
        else
        {
            if (a.GetPropertyCount() != b.GetPropertyCount()) { return false; }
            foreach (var property in a.EnumerateObject())
            {
                if (!b.TryGetProperty(property.Name, out var value)) { return false; }
                var equal = Equal(property.Value, value);
                if (equal is false) { return false; }
                if (equal is null) { unknown = true; }
            }
        }
        return unknown ? null : true;
    }
}
