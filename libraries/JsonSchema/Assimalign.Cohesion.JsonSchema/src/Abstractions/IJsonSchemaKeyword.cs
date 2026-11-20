namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The evaluator of a custom keyword, created once per occurrence of the keyword in a schema.
/// </summary>
/// <remarks>
/// A keyword is valid unless it calls <see cref="IJsonSchemaKeywordContext.Fail(string?)"/>. One
/// instance may evaluate many instances concurrently, so an implementation keeps no per-evaluation
/// state.
/// </remarks>
public interface IJsonSchemaKeyword
{
    /// <summary>
    /// Evaluates the keyword against the instance the context describes.
    /// </summary>
    /// <param name="context">The evaluation context, valid only for the duration of this call.</param>
    void Evaluate(IJsonSchemaKeywordContext context);
}
