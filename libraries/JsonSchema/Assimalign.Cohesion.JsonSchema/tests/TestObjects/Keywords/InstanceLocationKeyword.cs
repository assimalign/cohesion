namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Annotates with the location of the instance it evaluates.
/// </summary>
internal sealed class InstanceLocationKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context) => context.Annotate(TestJson.String(context.InstanceLocation.Value));
}
