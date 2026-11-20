using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>additionalItems</c> (drafts 4 to 2019-09): items past those an array-form <c>items</c> covers
/// must be valid against the subschema. Without an array-form <c>items</c> it is ignored.
/// </summary>
internal sealed class AdditionalItemsKeyword : SchemaKeyword
{
    public AdditionalItemsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public LegacyItemsKeyword? Items { get; private set; }

    public override int Priority => KeywordPriority.Items;

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            if (sibling is LegacyItemsKeyword items)
            {
                Items = items;
            }
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (Items?.Subschemas is not SchemaNode[] tuple || instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return ItemSequence.EvaluateRest(context, instance, Subschema, tuple.Length, Resources.AdditionalItemsFailed, Resources.AdditionalItemsNotAllowed);
    }
}
