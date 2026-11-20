namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Applies its one subschema to the current instance, as <c>allOf</c> does with one branch.
/// </summary>
internal sealed class ApplyInPlaceKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context)
    {
        if (!context.EvaluateSubschema(JsonPointer.Root))
        {
            context.Fail();
        }
    }
}
