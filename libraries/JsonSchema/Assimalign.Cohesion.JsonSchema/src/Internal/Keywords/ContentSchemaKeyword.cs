using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>contentSchema</c> (2020-12 Validation §8.5): an annotation on string instances, ignored without a sibling
/// <c>contentMediaType</c>. When the caller sets <see cref="JsonSchemaEvaluationOptions.ContentAssertion"/>,
/// decoded JSON content must be valid against the subschema.
/// </summary>
internal sealed class ContentSchemaKeyword : SchemaKeyword
{
    public ContentSchemaKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public ContentMediaTypeKeyword? MediaType { get; private set; }

    public override int Priority => KeywordPriority.Applicator;

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            if (sibling is ContentMediaTypeKeyword mediaType)
            {
                MediaType = mediaType;
            }
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (MediaType is null || instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        context.Annotate(Value);
        if (!MediaType.IsJson || !context.Options.ContentAssertion)
        {
            return true;
        }

        if (!ContentMediaTypeKeyword.TryDecode(instance, MediaType.Encoding, out byte[] content)
            || !ContentMediaTypeKeyword.TryParse(content, out JsonDocument? document))
        {
            return true;
        }

        using (document)
        {
            if (context.EvaluateDerived(Subschema, document.RootElement, null))
            {
                return true;
            }
        }

        context.Fail(Resources.ContentSchemaFailed);
        return false;
    }
}
