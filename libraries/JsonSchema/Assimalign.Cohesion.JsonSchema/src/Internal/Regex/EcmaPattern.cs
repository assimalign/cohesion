using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// A compiled ECMA-262 pattern: the .NET translation of <c>pattern</c> or a
/// <c>patternProperties</c> member name.
/// </summary>
/// <remarks>
/// A pattern compiles with <see cref="RegexOptions.NonBacktracking"/>, whose matching time is linear in
/// the input. Patterns that need backtracking (backreferences, lookarounds, and the lookarounds the
/// translation of <c>\b</c> uses) fall back to the backtracking engine, bounded by the document's match
/// timeout.
/// </remarks>
[DebuggerDisplay("{Pattern,nq}")]
internal sealed class EcmaPattern
{
    private EcmaPattern(string pattern, Regex regex, string location)
    {
        Pattern = pattern;
        Regex = regex;
        Location = location;
    }

    /// <summary>Gets the ECMA-262 pattern as written in the schema.</summary>
    public string Pattern { get; }

    /// <summary>Gets the translated expression.</summary>
    public Regex Regex { get; }

    /// <summary>Gets the schema location that declared the pattern, for errors.</summary>
    public string Location { get; }

    public static EcmaPattern Create(string pattern, TimeSpan matchTimeout, string location)
    {
        if (!EcmaRegexTranslator.TryTranslate(pattern, out string translated, out string? error))
        {
            throw JsonSchemaProcessingException.PatternInvalid(pattern, location, error!, null);
        }

        Regex regex;
        try
        {
            regex = new Regex(translated, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, matchTimeout);
        }
        catch (NotSupportedException)
        {
            try
            {
                regex = new Regex(translated, RegexOptions.CultureInvariant, matchTimeout);
            }
            catch (ArgumentException exception)
            {
                throw JsonSchemaProcessingException.PatternInvalid(pattern, location, exception.Message, exception);
            }
        }
        catch (ArgumentException exception)
        {
            throw JsonSchemaProcessingException.PatternInvalid(pattern, location, exception.Message, exception);
        }

        return new EcmaPattern(pattern, regex, location);
    }

    /// <summary>Determines whether the pattern matches anywhere in the input, as ECMA-262 <c>test</c> does.</summary>
    /// <exception cref="JsonSchemaException">The match exceeded the timeout.</exception>
    public bool IsMatch(ReadOnlySpan<char> input)
    {
        try
        {
            return Regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw JsonSchemaProcessingException.PatternTimeout(Pattern, Location, exception);
        }
    }
}
