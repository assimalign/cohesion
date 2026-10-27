using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    // Couchbase expression/case_searched.go:73-88 calls Truth(); value/integer.go:171-172,
    // string.go:183-185, array.go:254-256 and object.go:440-442 define non-Boolean truth.
    // Preserve unknown values for the four-valued AND/OR/NOT tables.
    private static object? Truth(object? value) => value switch
    {
        null or DocumentMissing or bool => value,
        decimal number => number != 0,
        long number => number != 0,
        string text => text.Length > 0,
        JsonElement { ValueKind: JsonValueKind.Array } array => array.GetArrayLength() > 0,
        JsonElement { ValueKind: JsonValueKind.Object } obj => obj.GetPropertyCount() > 0,
        _ => false,
    };

    // Couchbase value/object.go:770-799 and value/array.go:751-779:
    // a definite mismatch wins over nested NULL; otherwise NULL is not equality.
    // IN consumes only TRUE (expression/coll_in.go:103-115), as does simple CASE.
    private object? Equal(object? left, object? right) => ValuesEqual(left, right, _cancellationToken);

    internal static object? ValuesEqual(object? left, object? right, System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        left = left is JsonElement leftJson ? ReadJson(leftJson) : Normalize(left);
        right = right is JsonElement rightJson ? ReadJson(rightJson) : Normalize(right);
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
            var rightItems = b.EnumerateArray();
            foreach (var item in a.EnumerateArray())
            {
                rightItems.MoveNext();
                var equal = ValuesEqual(item, rightItems.Current, cancellationToken);
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
                var equal = ValuesEqual(property.Value, value, cancellationToken);
                if (equal is false) { return false; }
                if (equal is null) { unknown = true; }
            }
        }
        return unknown ? null : true;
    }
}
