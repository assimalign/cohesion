using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>format</c> (2020-12 Validation §7): an annotation by default. It asserts when the caller sets
/// <see cref="JsonSchemaEvaluationOptions.FormatAssertion"/>, where unknown formats pass, or when the
/// 2020-12 format-assertion vocabulary defines it, where unknown formats fail.
/// </summary>
/// <remarks>
/// A caller's format replaces the built-in one of the same name. A built-in format validates under the
/// rules of the dialect that declared the keyword: <c>hostname</c> checks A-labels from draft 7 on, and
/// <c>relative-json-pointer</c> allows index manipulation from 2020-12 on.
/// </remarks>
internal sealed class FormatKeyword : SchemaKeyword
{
    public FormatKeyword(SchemaNode owner, string name, JsonElement value, bool alwaysAssert)
        : base(owner, name, value)
    {
        FormatName = value.GetString()!;
        AlwaysAssert = alwaysAssert;
        BuiltIn = BuiltInFormats.Find(FormatName);
        Version = owner.Dialect.Version;
    }

    public string FormatName { get; }

    /// <summary>Gets a value indicating whether the format-assertion vocabulary defines the keyword.</summary>
    public bool AlwaysAssert { get; }

    /// <summary>Gets the built-in format of the keyword's name, if the library defines one.</summary>
    public BuiltInFormat? BuiltIn { get; }

    /// <summary>Gets the version of the dialect that declared the keyword.</summary>
    public JsonSchemaVersion Version { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        context.Annotate(Value);
        if (!AlwaysAssert && !context.Options.FormatAssertion)
        {
            return true;
        }

        bool valid;
        if (context.Options.TryGetCustomFormat(FormatName, out IJsonSchemaFormat? custom))
        {
            valid = custom.IsValid(instance);
        }
        else if (BuiltIn is not null)
        {
            valid = BuiltIn.IsValid(instance, Version);
        }
        else if (AlwaysAssert)
        {
            Fail(context, Resources.FormatUnknown);
            return false;
        }
        else
        {
            return true;
        }

        if (!valid)
        {
            Fail(context, Resources.FormatInvalid);
        }

        return valid;
    }

    private void Fail(EvaluationContext context, string format)
    {
        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, format, FormatName));
        }
    }
}
