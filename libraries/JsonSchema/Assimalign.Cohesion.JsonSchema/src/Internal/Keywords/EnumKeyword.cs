using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>enum</c> (2020-12 Validation §6.1.2): the instance must equal one of the listed values, by
/// JSON Schema equality.
/// </summary>
internal sealed class EnumKeyword : SchemaKeyword
{
    // Past this many values a hash set beats comparing against each in turn.
    private const int hashThreshold = 8;

    public EnumKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
        if (value.GetArrayLength() > hashThreshold)
        {
            Values = new HashSet<JsonElement>(value.EnumerateArray(), JsonEquality.Comparer.Instance);
        }
    }

    /// <summary>Gets the values as a set when the list is long enough to hash, or <see langword="null"/>.</summary>
    public HashSet<JsonElement>? Values { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (Values is not null)
        {
            if (Values.Contains(instance))
            {
                return true;
            }
        }
        else
        {
            foreach (JsonElement candidate in Value.EnumerateArray())
            {
                if (JsonEquality.AreEqual(candidate, instance))
                {
                    return true;
                }
            }
        }

        context.Fail(Resources.EnumMismatch);
        return false;
    }
}
