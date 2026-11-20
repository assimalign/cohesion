using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>patternProperties</c> (2020-12 Core §10.3.2.2): each member whose name matches a pattern must
/// be valid against that pattern's subschema. The annotation is the names that matched.
/// </summary>
internal sealed class PatternPropertiesKeyword : SchemaKeyword
{
    public PatternPropertiesKeyword(SchemaNode owner, string name, JsonElement value, Entry[] entries)
        : base(owner, name, value)
    {
        Entries = entries;
    }

    public Entry[] Entries { get; }

    public override int Priority => KeywordPriority.Properties;

    public bool Matches(ReadOnlySpan<char> memberName)
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Pattern.IsMatch(memberName))
            {
                return true;
            }
        }

        return false;
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object || Entries.Length == 0)
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
            bool matched = false;
            foreach (Entry entry in Entries)
            {
                if (!entry.Pattern.IsMatch(memberName))
                {
                    continue;
                }

                matched = true;
                if (!context.EvaluateProperty(entry.Subschema, member))
                {
                    if (context.ShortCircuit)
                    {
                        return false;
                    }

                    valid = false;
                }
            }

            if (matched)
            {
                context.MarkProperty(position);
                if (context.RecordsOutput)
                {
                    (applied ??= new()).Add(member.Name);
                }
            }

            position++;
        }

        if (!valid)
        {
            context.Fail(Resources.PatternPropertiesFailed);
            return false;
        }

        if (applied is not null)
        {
            context.Annotate(JsonValues.CreateStringArray(applied));
        }

        return true;
    }

    /// <summary>One pattern and the subschema the members it matches must satisfy.</summary>
    public sealed class Entry
    {
        public Entry(EcmaPattern pattern, SchemaNode subschema)
        {
            Pattern = pattern;
            Subschema = subschema;
        }

        public EcmaPattern Pattern { get; }

        public SchemaNode Subschema { get; }
    }
}
