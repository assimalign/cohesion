using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>properties</c> (2020-12 Core §10.3.2.1): each member named in the keyword must be valid against
/// its subschema. The annotation is the names it applied to.
/// </summary>
internal sealed class PropertiesKeyword : SchemaKeyword
{
    public PropertiesKeyword(SchemaNode owner, string name, JsonElement value, Dictionary<string, SchemaNode> subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
        Lookup = subschemas.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public Dictionary<string, SchemaNode> Subschemas { get; }

    /// <summary>Gets a view of <see cref="Subschemas"/> that member names decoded into a buffer can query.</summary>
    public Dictionary<string, SchemaNode>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

    public override int Priority => KeywordPriority.Properties;

    public bool Contains(ReadOnlySpan<char> memberName) => Lookup.ContainsKey(memberName);

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object || Subschemas.Count == 0)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[JsonText.BufferLength];
        List<string>? applied = null;
        bool valid = true;
        int position = 0;
        foreach (JsonProperty member in instance.EnumerateObject())
        {
            if (Lookup.TryGetValue(JsonText.GetName(member, buffer), out SchemaNode? subschema))
            {
                context.MarkProperty(position);
                if (context.RecordsOutput)
                {
                    (applied ??= new()).Add(member.Name);
                }

                if (!context.EvaluateProperty(subschema, member))
                {
                    if (context.ShortCircuit)
                    {
                        return false;
                    }

                    valid = false;
                }
            }

            position++;
        }

        if (!valid)
        {
            context.Fail(Resources.PropertiesFailed);
            return false;
        }

        if (applied is not null)
        {
            context.Annotate(JsonValues.CreateStringArray(applied));
        }

        return true;
    }
}
