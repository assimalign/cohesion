using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>additionalProperties</c> (2020-12 Core §10.3.2.3): members that neither a sibling
/// <c>properties</c> names nor a sibling <c>patternProperties</c> pattern matches must be valid
/// against the subschema. The annotation is the names it applied to.
/// </summary>
internal sealed class AdditionalPropertiesKeyword : SchemaKeyword
{
    public AdditionalPropertiesKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public PropertiesKeyword? Properties { get; private set; }

    public PatternPropertiesKeyword? PatternProperties { get; private set; }

    public override int Priority => KeywordPriority.Properties;

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            if (sibling is PropertiesKeyword properties)
            {
                Properties = properties;
            }
            else if (sibling is PatternPropertiesKeyword patternProperties)
            {
                PatternProperties = patternProperties;
            }
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (Subschema.BooleanValue == true && !context.CollectsAnnotations)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[JsonText.BufferLength];
        List<string>? applied = null;
        bool valid = true;
        int position = 0;
        foreach (JsonProperty member in instance.EnumerateObject())
        {
            ReadOnlySpan<char> memberName = JsonText.GetName(member, buffer);
            if ((Properties is not null && Properties.Contains(memberName))
                || (PatternProperties is not null && PatternProperties.Matches(memberName)))
            {
                position++;
                continue;
            }

            context.MarkProperty(position);
            if (context.RecordsOutput)
            {
                (applied ??= new()).Add(member.Name);
            }

            if (!context.EvaluateProperty(Subschema, member))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                valid = false;
            }

            position++;
        }

        if (!valid)
        {
            context.Fail(Subschema.BooleanValue == false ? Resources.AdditionalPropertiesNotAllowed : Resources.AdditionalPropertiesFailed);
            return false;
        }

        if (applied is not null)
        {
            context.Annotate(JsonValues.CreateStringArray(applied));
        }

        return true;
    }
}
