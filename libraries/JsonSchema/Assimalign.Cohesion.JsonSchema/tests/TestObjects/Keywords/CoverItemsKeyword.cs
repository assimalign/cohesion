using System;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Applies the schemas of its array value to the instance items at the same indexes and marks them
/// evaluated, as <c>prefixItems</c> does.
/// </summary>
internal sealed class CoverItemsKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context)
    {
        if (context.Instance.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int count = Math.Min(context.KeywordValue.GetArrayLength(), context.Instance.GetArrayLength());
        bool valid = true;
        for (int index = 0; index < count; index++)
        {
            valid &= context.EvaluateSubschema(JsonPointer.Root.Append(index), index);
            context.MarkItemEvaluated(index);
        }

        if (!valid)
        {
            context.Fail();
        }
    }
}
