using System;
using System.Diagnostics;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Determines whether a string satisfies a built-in format under a dialect's rules.
/// </summary>
/// <param name="value">The string.</param>
/// <param name="version">The version of the dialect that declared the <c>format</c> keyword.</param>
internal delegate bool FormatValidator(ReadOnlySpan<char> value, JsonSchemaVersion version);

/// <summary>
/// A format the library defines (2020-12 Validation §7.3). Every built-in format constrains strings
/// only; other instance types satisfy it.
/// </summary>
[DebuggerDisplay("{Name,nq}")]
internal sealed class BuiltInFormat
{
    public BuiltInFormat(string name, FormatValidator validator)
    {
        Name = name;
        Validator = validator;
    }

    /// <summary>Gets the format name, as written in the <c>format</c> keyword.</summary>
    public string Name { get; }

    /// <summary>Gets the string validator.</summary>
    public FormatValidator Validator { get; }

    /// <summary>
    /// Determines whether an instance satisfies the format: strings are validated, every other type
    /// passes.
    /// </summary>
    public bool IsValid(JsonElement instance, JsonSchemaVersion version)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[JsonText.BufferLength];
        return Validator(JsonText.GetString(instance, buffer), version);
    }
}
