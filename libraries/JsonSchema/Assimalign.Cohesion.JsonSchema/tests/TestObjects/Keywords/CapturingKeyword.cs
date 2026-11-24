namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Keeps the context it last evaluated with, so a test can use it after the evaluation returned.
/// </summary>
internal sealed class CapturingKeyword : IJsonSchemaKeyword
{
    public IJsonSchemaKeywordContext? Captured { get; private set; }

    public void Evaluate(IJsonSchemaKeywordContext context) => Captured = context;
}
