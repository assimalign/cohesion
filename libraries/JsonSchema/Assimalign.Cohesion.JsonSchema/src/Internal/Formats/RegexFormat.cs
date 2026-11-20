using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>regex</c> format: an ECMA-262 regular expression (2020-12 Validation §7.3.8), read with the
/// same translator the <c>pattern</c> keyword uses, so a string is a valid <c>regex</c> exactly when
/// it would load as a <c>pattern</c>.
/// </summary>
/// <remarks>
/// Only the syntax is checked; nothing is matched, so no input can make validation slow.
/// </remarks>
internal static class RegexFormat
{
    public static bool IsRegex(ReadOnlySpan<char> value, JsonSchemaVersion version) =>
        EcmaRegexTranslator.TryTranslate(value.ToString(), out _, out _);
}
