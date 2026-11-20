using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// A keyword a caller defined with <see cref="JsonSchemaKeywordDefinition.Create"/>: its
/// <see cref="IJsonSchemaKeyword"/> evaluator and the subschemas its layout placed.
/// </summary>
internal sealed class CustomKeyword : SchemaKeyword
{
    public CustomKeyword(SchemaNode owner, string name, JsonElement value, IJsonSchemaKeyword evaluator, Dictionary<string, SchemaNode> subschemas)
        : base(owner, name, value)
    {
        Evaluator = evaluator;
        Subschemas = subschemas;
    }

    public IJsonSchemaKeyword Evaluator { get; }

    /// <summary>Gets the subschemas, keyed by their JSON Pointer relative to the keyword's value.</summary>
    public Dictionary<string, SchemaNode> Subschemas { get; }

    public override int Priority => KeywordPriority.Custom;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        KeywordContext keywordContext = new(context, this, instance);
        try
        {
            Evaluator.Evaluate(keywordContext);
        }
        finally
        {
            keywordContext.Close();
        }

        if (keywordContext.Failed)
        {
            if (context.RecordsOutput)
            {
                context.Fail(keywordContext.Message ?? string.Format(CultureInfo.CurrentCulture, Resources.CustomKeywordFailed, Name));
            }

            return false;
        }

        if (keywordContext.Annotation is JsonElement annotation)
        {
            context.Annotate(annotation);
        }

        return true;
    }
}
