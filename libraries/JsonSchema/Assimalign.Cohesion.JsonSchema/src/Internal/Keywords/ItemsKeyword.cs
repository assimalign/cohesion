using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>items</c> as 2020-12 defines it (Core §10.3.1.2): every item after those <c>prefixItems</c>
/// covers must be valid against the subschema. The annotation is <see langword="true"/> when it
/// applied to any item.
/// </summary>
internal sealed class ItemsKeyword : SchemaKeyword
{
    public ItemsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public PrefixItemsKeyword? PrefixItems { get; private set; }

    public override int Priority => KeywordPriority.Items;

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            if (sibling is PrefixItemsKeyword prefixItems)
            {
                PrefixItems = prefixItems;
            }
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        int start = PrefixItems?.Subschemas.Length ?? 0;
        return ItemSequence.EvaluateRest(context, instance, Subschema, start, Resources.ItemsFailed, Resources.ItemsNotAllowed);
    }
}
