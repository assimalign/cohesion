using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>dependencies</c>, the drafts 4 to 7 form of <c>dependentRequired</c> and
/// <c>dependentSchemas</c> combined: each member the keyword names that the instance has requires
/// either other members or a valid subschema.
/// </summary>
internal sealed class DependenciesKeyword : SchemaKeyword
{
    public DependenciesKeyword(SchemaNode owner, string name, JsonElement value, Dependency[] dependencies)
        : base(owner, name, value)
    {
        Dependencies = dependencies;
    }

    public Dependency[] Dependencies { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        string? error = null;
        foreach (Dependency dependency in Dependencies)
        {
            if (!instance.TryGetProperty(dependency.Name, out _))
            {
                continue;
            }

            if (dependency.Required is string[] required)
            {
                List<string>? missing = null;
                foreach (string member in required)
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
                    error ??= string.Format(CultureInfo.CurrentCulture, Resources.DependentRequiredMissing, dependency.Name, string.Join(", ", missing));
                }
            }
            else if (!context.EvaluateInPlace(dependency.Subschema!, instance))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                error ??= Resources.DependentSchemasFailed;
            }
        }

        if (error is not null)
        {
            context.Fail(error);
            return false;
        }

        return true;
    }

    /// <summary>One member's dependency: the members it requires, or the subschema it applies.</summary>
    public sealed class Dependency
    {
        public Dependency(string name, string[]? required, SchemaNode? subschema)
        {
            Name = name;
            Required = required;
            Subschema = subschema;
        }

        public string Name { get; }

        public string[]? Required { get; }

        public SchemaNode? Subschema { get; }
    }
}
