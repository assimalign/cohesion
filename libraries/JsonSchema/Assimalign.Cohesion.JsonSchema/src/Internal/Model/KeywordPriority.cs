namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Evaluation order within one schema object.
/// </summary>
internal static class KeywordPriority
{
    public const int Assertion = 10;
    public const int Reference = 20;
    public const int Items = 30;
    public const int Properties = 40;
    public const int Applicator = 50;
    public const int ConditionalBranch = 51;
    public const int Custom = 60;
    public const int Unevaluated = 70;
    public const int Annotation = 80;
}
