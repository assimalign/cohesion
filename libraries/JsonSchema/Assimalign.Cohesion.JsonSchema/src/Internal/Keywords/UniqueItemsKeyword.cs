using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>"uniqueItems": true</c> (2020-12 Validation §6.4.3): no two items may be equal by JSON Schema
/// equality, so <c>1</c> and <c>1.0</c> are duplicates and objects compare regardless of member order.
/// </summary>
internal sealed class UniqueItemsKeyword : SchemaKeyword
{
    // Up to this many items, comparing every pair is cheaper than hashing.
    private const int pairwiseThreshold = 16;

    public UniqueItemsKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        int length = instance.GetArrayLength();
        if (length < 2)
        {
            return true;
        }

        JsonElement[] items = ArrayPool<JsonElement>.Shared.Rent(length);
        try
        {
            int count = 0;
            foreach (JsonElement item in instance.EnumerateArray())
            {
                items[count++] = item;
            }

            if (!TryFindDuplicate(items, count, out int first, out int second))
            {
                return true;
            }

            if (context.RecordsOutput)
            {
                context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.UniqueItemsFailed, first, second));
            }

            return false;
        }
        finally
        {
            ArrayPool<JsonElement>.Shared.Return(items, clearArray: true);
        }
    }

    private static bool TryFindDuplicate(JsonElement[] items, int count, out int first, out int second)
    {
        if (count <= pairwiseThreshold)
        {
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (JsonEquality.AreEqual(items[i], items[j]))
                    {
                        first = i;
                        second = j;
                        return true;
                    }
                }
            }
        }
        else
        {
            Dictionary<JsonElement, int> seen = new(count, JsonEquality.Comparer.Instance);
            for (int i = 0; i < count; i++)
            {
                if (!seen.TryAdd(items[i], i))
                {
                    first = seen[items[i]];
                    second = i;
                    return true;
                }
            }
        }

        first = -1;
        second = -1;
        return false;
    }
}
