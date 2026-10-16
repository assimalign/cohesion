using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace System.IO;

/// <summary>
/// Represents a parsed glob pattern that matches strings against wildcards, character sets,
/// ranges and brace groups.
/// </summary>
/// <remarks>
/// A glob is a general string matcher, not a file-system API: <c>/</c> and <c>\</c> both separate
/// segments, and matching is case-sensitive unless the caller asks otherwise. Create one with
/// <see cref="Parse(string)"/> or the implicit conversion from <see cref="string"/>.
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public sealed partial class Glob
{
    private readonly TokenBase[] _tokens;

    internal Glob(TokenBase[] tokens)
    {
        _tokens = ArgumentNullException.ThrowIfNullOrNone(tokens);
    }

    /// <summary>
    /// Gets the number of tokens in the glob.
    /// </summary>
    public int Count => _tokens.Length;

    /// <summary>
    /// Gets the parsed tokens, in pattern order.
    /// </summary>
    /// <remarks>
    /// Globs parsed from the same pattern share one token array through the parse cache. The
    /// returned <see cref="ImmutableArray{T}"/> wraps that array without copying it and exposes no
    /// way to change it, so reading the tokens allocates nothing and cannot alter another glob.
    /// </remarks>
    public ImmutableArray<Token> Tokens => ImmutableArray<Token>.CastUp(ImmutableCollectionsMarshal.AsImmutableArray(_tokens));

    #region Methods

    /// <summary>
    /// Determines whether a value matches the glob, comparing characters case-sensitively.
    /// </summary>
    /// <param name="value">The text to test, such as a path. <c>/</c> and <c>\</c> both separate segments.</param>
    /// <returns>
    /// <see langword="true"/> if the whole of <paramref name="value"/> matches the pattern;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Same as <see cref="IsMatch(ReadOnlySpan{char}, CultureInfo, bool)"/> with
    /// <see cref="CultureInfo.InvariantCulture"/> and case-sensitive matching.
    /// </remarks>
    public bool IsMatch(ReadOnlySpan<char> value)
    {
        return IsMatch(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Determines whether a value matches the glob, optionally ignoring case.
    /// </summary>
    /// <param name="value">The text to test, such as a path. <c>/</c> and <c>\</c> both separate segments.</param>
    /// <param name="caseInSensitive">
    /// <see langword="true"/> to ignore case, using the casing rules of
    /// <see cref="CultureInfo.InvariantCulture"/>; <see langword="false"/> to compare characters
    /// ordinally.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the whole of <paramref name="value"/> matches the pattern;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Same as <see cref="IsMatch(ReadOnlySpan{char}, CultureInfo, bool)"/> with
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    public bool IsMatch(ReadOnlySpan<char> value, bool caseInSensitive)
    {
        return IsMatch(value, CultureInfo.InvariantCulture, caseInSensitive);
    }

    /// <summary>
    /// Determines whether a value matches the glob, comparing characters case-sensitively.
    /// </summary>
    /// <param name="value">The text to test, such as a path. <c>/</c> and <c>\</c> both separate segments.</param>
    /// <param name="cultureInfo">
    /// The culture to match under. This overload matches case-sensitively, comparing characters
    /// ordinally, so the culture does not affect the result.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the whole of <paramref name="value"/> matches the pattern;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Same as <see cref="IsMatch(ReadOnlySpan{char}, CultureInfo, bool)"/> with case-sensitive
    /// matching, so it returns what <see cref="IsMatch(ReadOnlySpan{char})"/> returns.
    /// </remarks>
    public bool IsMatch(ReadOnlySpan<char> value, CultureInfo cultureInfo)
    {
        return IsMatch(value, cultureInfo, false);
    }

    /// <summary>
    /// Determines whether a value matches the glob, using a culture's casing rules when ignoring
    /// case.
    /// </summary>
    /// <param name="value">The text to test, such as a path. <c>/</c> and <c>\</c> both separate segments.</param>
    /// <param name="cultureInfo">
    /// The culture whose casing rules apply when <paramref name="caseInSensitive"/> is
    /// <see langword="true"/>. Case-sensitive matching compares characters ordinally and does not
    /// use it.
    /// </param>
    /// <param name="caseInSensitive">
    /// <see langword="true"/> to ignore case; <see langword="false"/> to compare characters
    /// ordinally.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the whole of <paramref name="value"/> matches the pattern;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Case-insensitive matching applies the casing rules of <paramref name="cultureInfo"/>, so
    /// the result can depend on the culture: under <c>tr-TR</c>, the pattern <c>FILE</c> does not
    /// match <c>file</c>.
    /// </remarks>
    public bool IsMatch(ReadOnlySpan<char> value, CultureInfo cultureInfo, bool caseInSensitive)
    {
        bool consumesVariableLength = _tokens.Length == 0;
        int consumesMinLength = 0;
        int position = 0;

        for (int i = 0; i < _tokens.Length; i++)
        {
            consumesMinLength = consumesMinLength + _tokens[i].ConsumesMinLength;

            if (!consumesVariableLength)
            {
                if (_tokens[i].ConsumesVariableLength)
                {
                    consumesVariableLength = true;
                }
            }
        }

        if (!consumesVariableLength)
        {
            if ((value.Length - position) != consumesMinLength)
            {
                // can't possibly match as tokens require a fixed length and the string length is different.
                return false;
            }
        }
        else if ((value.Length - position) < consumesMinLength)
        {
            // can't possibly match as tokens require a minimum length and the string is too short.
            return false;
        }

        for (int i = 0; i < _tokens.Length; i++)
        {
            if (!_tokens[i].Test(value, cultureInfo, caseInSensitive, position, out position))
            {
                return false;
            }
        }

        // if all tokens matched but still more text then fail!
        if (position < value.Length)
        {
            return false;
        }

        // Success.
        return true;
    }

    /// <summary>
    /// Returns a Glob object from the provided pattern.
    /// </summary>
    /// <param name="pattern">The glob pattern to parse.</param>
    /// <returns>A glob that matches <paramref name="pattern"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is empty, or has a brace group with no alternatives: <c>{}</c>,
    /// <c>{,}</c>, or a <c>{</c> that ends the pattern.
    /// </exception>
    /// <remarks>
    /// Safe to call from any number of threads at once. Parsed patterns are cached process-wide,
    /// so parsing a cached pattern again allocates only the returned glob.
    /// </remarks>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(pattern);

        TokenBase[] tokens = Parser.Tokenize(pattern);

        return new Glob(tokens);
    }

    #region Overloads

    /// <summary>
    /// Returns the pattern text of the glob.
    /// </summary>
    /// <returns>The pattern rebuilt from its tokens, with each path separator written as <c>/</c>.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();

        for(int i = 0; i < _tokens.Length; i++)
        {
            Format(builder, _tokens[i]);
        }

        return builder.ToString();
    }

    private static StringBuilder Format(StringBuilder builder, TokenBase token)
    {
        if (token is CompositeGlobToken composite)
        {
            builder.Append(token.Value);

            for (int i = 0; i < composite.Tokens.Length; i++)
            {
                Format(builder, composite.Tokens[i]);
            }

            return builder;
        }

        return builder.Append(token.Value);
    }

    /// <summary>
    /// Returns a hash code for the pattern text.
    /// </summary>
    /// <returns>The hash code of the string that <see cref="ToString()"/> returns.</returns>
    /// <remarks>
    /// <see cref="Glob"/> does not override <see cref="object.Equals(object?)"/>, so two globs
    /// parsed from the same pattern have the same hash code but are not equal.
    /// </remarks>
    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    #endregion

    #endregion

    #region Operators

    /// <summary>
    /// Parses a pattern into a <see cref="Glob"/>.
    /// </summary>
    /// <param name="pattern">The glob pattern to parse.</param>
    /// <returns>The glob that <see cref="Parse(string)"/> returns for <paramref name="pattern"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is empty, or has a brace group with no alternatives: <c>{}</c>,
    /// <c>{,}</c>, or a <c>{</c> that ends the pattern.
    /// </exception>
    public static implicit operator Glob(string pattern)
    {
        return Parse(pattern);
    }

    /// <summary>
    /// Converts a glob to its pattern text.
    /// </summary>
    /// <param name="glob">The glob to convert.</param>
    /// <returns>The pattern text that <see cref="ToString()"/> returns.</returns>
    public static implicit operator string(Glob glob)
    {
        return glob.ToString();
    }

    #endregion

    #region Partials

    /// <summary>
    /// Identifies the pattern element that a <see cref="Token"/> represents.
    /// </summary>
    public enum TokenKind
    {
        /// <summary>
        /// Literal text, such as <c>.txt</c> in <c>*.txt</c>, which matches itself.
        /// </summary>
        Literal,

        /// <summary>
        /// A character set, <c>[abc]</c>, which matches one of the listed characters, or a negated
        /// set, <c>[!abc]</c>, which matches one character that is not listed.
        /// </summary>
        CharacterSet,

        /// <summary>
        /// <c>?</c>, which matches any one character except a path separator.
        /// </summary>
        Any,

        /// <summary>
        /// <c>*</c>, which matches zero or more characters within one segment. It does not match a
        /// path separator.
        /// </summary>
        Wildcard,

        /// <summary>
        /// <c>**</c>, which matches zero or more whole segments. The token includes any path
        /// separator next to it, as in <c>/**/</c>.
        /// </summary>
        WildcardDirectory,

        /// <summary>
        /// A character range, <c>[a-z]</c> or <c>[0-9]</c>, which matches one character in the
        /// range, bounds included, or a negated range, <c>[!a-z]</c>, which matches one character
        /// outside it.
        /// </summary>
        Range,

        /// <summary>
        /// A path separator, <c>/</c> or <c>\</c>, which matches either separator.
        /// </summary>
        PathSeparator,

        /// <summary>
        /// A brace group, such as <c>{md,cs}</c>, which matches one of its comma-separated
        /// alternatives, trying them in order. Each alternative is literal text, <c>?</c>, or a
        /// character set or range.
        /// </summary>
        BraceGrouping
    }

    /// <summary>
    /// Represents one element of a parsed glob pattern, such as literal text, a wildcard or a
    /// character set.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Glob"/> creates tokens. Read them through <see cref="Glob.Tokens"/>.
    /// </remarks>
    public abstract class Token
    {
        internal Token() { }

        /// <summary>
        /// The raw token value.
        /// </summary>
        public abstract string Value { get; }

        /// <summary>
        /// The token kind.
        /// </summary>
        public abstract TokenKind Kind { get; }
    }

    #endregion
}