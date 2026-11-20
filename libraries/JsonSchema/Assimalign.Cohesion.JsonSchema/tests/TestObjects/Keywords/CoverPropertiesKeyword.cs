using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Applies the member schemas of its value to the instance members of the same names and marks them
/// evaluated, as <c>properties</c> does.
/// </summary>
internal sealed class CoverPropertiesKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context)
    {
        if (context.Instance.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        bool valid = true;
        foreach (JsonProperty schema in context.KeywordValue.EnumerateObject())
        {
            if (!context.Instance.TryGetProperty(schema.Name, out _))
            {
                continue;
            }

            valid &= context.EvaluateSubschema(JsonPointer.Root.Append(schema.Name), schema.Name);
            context.MarkPropertyEvaluated(schema.Name);
        }

        if (!valid)
        {
            context.Fail();
        }
    }
}
