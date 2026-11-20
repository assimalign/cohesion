using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// <c>"x-even": true</c> fails odd integers, with its own message or the generic one.
/// </summary>
internal sealed class EvenKeyword : IJsonSchemaKeyword
{
    public const string Message = "The number is odd.";

    private readonly bool _genericMessage;

    public EvenKeyword(bool genericMessage = false)
    {
        _genericMessage = genericMessage;
    }

    public void Evaluate(IJsonSchemaKeywordContext context)
    {
        if (context.KeywordValue.ValueKind == JsonValueKind.True
            && context.Instance.ValueKind == JsonValueKind.Number
            && context.Instance.TryGetInt64(out long value)
            && value % 2 != 0)
        {
            context.Fail(_genericMessage ? null : Message);
        }
    }
}
