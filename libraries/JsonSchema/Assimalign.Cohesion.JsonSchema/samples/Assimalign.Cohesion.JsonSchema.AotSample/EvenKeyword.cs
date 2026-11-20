using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.AotSample;

/// <summary><c>"x-even": true</c> fails odd integers.</summary>
internal sealed class EvenKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context)
    {
        if (context.KeywordValue.ValueKind == JsonValueKind.True
            && context.Instance.ValueKind == JsonValueKind.Number
            && context.Instance.TryGetInt64(out long value)
            && value % 2 != 0)
        {
            context.Fail("The number is odd.");
        }
    }
}
