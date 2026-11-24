using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>dependentRequired</c> (2020-12 Validation §6.5.4): each listed member the object has requires
/// the members listed for it.
/// </summary>
internal sealed class DependentRequiredKeyword : SchemaKeyword
{
    public DependentRequiredKeyword(SchemaNode owner, string name, JsonElement value, KeyValuePair<string, string[]>[] dependencies)
        : base(owner, name, value)
    {
        Dependencies = dependencies;
    }

    public KeyValuePair<string, string[]>[] Dependencies { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        string? error = null;
        foreach (KeyValuePair<string, string[]> dependency in Dependencies)
        {
            if (!instance.TryGetProperty(dependency.Key, out _))
            {
                continue;
            }

            List<string>? missing = null;
            foreach (string member in dependency.Value)
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

            if (missing is not null)
            {
                error ??= string.Format(CultureInfo.CurrentCulture, Resources.DependentRequiredMissing, dependency.Key, string.Join(", ", missing));
            }
        }

        if (error is null)
        {
            return true;
        }

        context.Fail(error);
        return false;
    }
}
