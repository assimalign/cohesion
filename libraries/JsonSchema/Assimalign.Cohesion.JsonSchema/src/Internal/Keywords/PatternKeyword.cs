using System;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>pattern</c> (2020-12 Validation §6.3.3): a string must match the ECMA-262 regular expression
/// somewhere; the pattern is not implicitly anchored.
/// </summary>
internal sealed class PatternKeyword : SchemaKeyword
{
    public PatternKeyword(SchemaNode owner, string name, JsonElement value, EcmaPattern pattern)
        : base(owner, name, value)
    {
        Pattern = pattern;
    }

    public EcmaPattern Pattern { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[JsonText.BufferLength];
        if (Pattern.IsMatch(JsonText.GetString(instance, buffer)))
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.PatternMismatch, Pattern.Pattern));
        }

        return false;
    }
}
