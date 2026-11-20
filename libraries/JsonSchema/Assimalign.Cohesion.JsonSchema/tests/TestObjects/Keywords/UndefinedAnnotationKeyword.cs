namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Annotates with the default JSON element, which holds no value.
/// </summary>
internal sealed class UndefinedAnnotationKeyword : IJsonSchemaKeyword
{
    public void Evaluate(IJsonSchemaKeywordContext context) => context.Annotate(default);
}
