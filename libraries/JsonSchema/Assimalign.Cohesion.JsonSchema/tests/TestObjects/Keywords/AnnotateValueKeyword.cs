namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Annotates with the keyword's own value, as <c>title</c> does.
/// </summary>
internal sealed class AnnotateValueKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context) => context.Annotate(context.KeywordValue);
}
