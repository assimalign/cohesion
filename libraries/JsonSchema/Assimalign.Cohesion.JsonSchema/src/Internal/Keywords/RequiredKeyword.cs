using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>required</c> (2020-12 Validation §6.5.3): an object must have every listed member.
/// </summary>
internal sealed class RequiredKeyword : SchemaKeyword
{
    public RequiredKeyword(SchemaNode owner, string name, JsonElement value, string[] members)
        : base(owner, name, value)
    {
        Members = members;
    }

    public string[] Members { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        List<string>? missing = null;
        foreach (string member in Members)
        {
            if (!instance.TryGetProperty(member, out _))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                (missing ??= new()).Add(member);
            }
        }

        if (missing is null)
        {
            return true;
        }

        context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.RequiredMissing, string.Join(", ", missing)));
        return false;
    }
}
