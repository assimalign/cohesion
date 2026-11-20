using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A JSON Pointer (RFC 6901): a string that identifies one value within a JSON document.
/// </summary>
/// <remarks>
/// <para>
/// A pointer is either the empty string, which identifies the whole document, or a sequence of
/// reference tokens, each prefixed with <c>/</c>. Inside a token <c>~0</c> encodes <c>~</c> and
/// <c>~1</c> encodes <c>/</c>; a <c>~</c> followed by anything else makes the pointer invalid.
/// </para>
/// <para>
/// Pointers compare ordinally, because RFC 6901 tokens are case-sensitive and the RFC defines no
/// normalization. <see langword="default"/> holds no pointer: members that need one throw
/// <see cref="InvalidOperationException"/>, and APIs that accept a pointer refuse it. Use
/// <see cref="Root"/> for the whole document.
/// </para>
/// </remarks>
public readonly partial struct JsonPointer
{
    private static readonly string[] _noTokens = [];

    /// <summary>
    /// Gets the pointer that identifies the whole document: the empty string.
    /// </summary>
    public static JsonPointer Root { get; } = new JsonPointer(string.Empty, validated: true);

    /// <summary>
    /// Determines whether a value is a syntactically valid JSON Pointer (RFC 6901 §3).
    /// </summary>
    /// <param name="value">The candidate pointer.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> for a valid pointer.</param>
    /// <returns><see langword="true"/> when the value is a valid pointer.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is null)
        {
            message = ErrorMessages.JsonPointerNull;
            return false;
        }

        if (value.Length == 0)
        {
            message = null;
            return true;
        }

        if (value[0] != '/')
        {
            message = ErrorMessages.JsonPointerNoLeadingSlash;
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '~' && (i + 1 == value.Length || (value[i + 1] != '0' && value[i + 1] != '1')))
            {
                message = ErrorMessages.JsonPointerEscapeInvalid;
                return false;
            }
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether this pointer identifies the whole document.
    /// </summary>
    /// <remarks><see langword="default"/> is not the root: it holds no pointer at all.</remarks>
    public bool IsRoot => Value is { Length: 0 };

    /// <summary>
    /// Returns the pointer's reference tokens, unescaped, in order.
    /// </summary>
    /// <returns>The tokens; empty for <see cref="Root"/>.</returns>
    /// <exception cref="InvalidOperationException">The pointer is <see langword="default"/>.</exception>
    public string[] GetTokens()
    {
        string value = RequireValue();
        if (value.Length == 0)
        {
            return _noTokens;
        }

        List<string> tokens = new();
        int start = 1;
        while (true)
        {
            int end = value.IndexOf('/', start);
            ReadOnlySpan<char> token = end < 0 ? value.AsSpan(start) : value.AsSpan(start, end - start);
            tokens.Add(UnescapeToken(token));
            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// Returns a pointer that extends this one with an object member name.
    /// </summary>
    /// <param name="token">The unescaped member name.</param>
    /// <returns>The extended pointer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The pointer is <see langword="default"/>.</exception>
    public JsonPointer Append(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new JsonPointer(RequireValue() + "/" + EscapeToken(token), validated: true);
    }

    /// <summary>
    /// Returns a pointer that extends this one with an array index.
    /// </summary>
    /// <param name="index">The zero-based array index.</param>
    /// <returns>The extended pointer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The pointer is <see langword="default"/>.</exception>
    public JsonPointer Append(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new JsonPointer(RequireValue() + "/" + index.ToString(CultureInfo.InvariantCulture), validated: true);
    }

    /// <summary>
    /// Creates a pointer from unescaped reference tokens.
    /// </summary>
    /// <param name="tokens">The tokens, in order.</param>
    /// <returns>The pointer; <see cref="Root"/> when there are no tokens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> or one of its items is <see langword="null"/>.</exception>
    public static JsonPointer FromTokens(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        StringBuilder builder = new();
        foreach (string token in tokens)
        {
            ArgumentNullException.ThrowIfNull(token, nameof(tokens));
            builder.Append('/').Append(EscapeToken(token));
        }

        return new JsonPointer(builder.ToString(), validated: true);
    }

    /// <summary>
    /// Resolves this pointer against a JSON document (RFC 6901 §4).
    /// </summary>
    /// <param name="document">The document, or any value within one, to resolve against.</param>
    /// <param name="value">The identified value when resolution succeeds; otherwise <see langword="default"/>.</param>
    /// <returns>
    /// <see langword="true"/> when every token names an existing member or array element. Array
    /// tokens must be decimal indexes without leading zeros; the <c>-</c> token never resolves.
    /// </returns>
    /// <exception cref="InvalidOperationException">The pointer is <see langword="default"/>.</exception>
    public bool TryEvaluate(JsonElement document, out JsonElement value)
    {
        string pointer = RequireValue();
        value = document;
        if (pointer.Length == 0)
        {
            return true;
        }

        int start = 1;
        while (true)
        {
            int end = pointer.IndexOf('/', start);
            ReadOnlySpan<char> raw = end < 0 ? pointer.AsSpan(start) : pointer.AsSpan(start, end - start);
            if (!TryStep(ref value, raw))
            {
                value = default;
                return false;
            }

            if (end < 0)
            {
                return true;
            }

            start = end + 1;
        }
    }

    /// <summary>
    /// Returns the URI fragment form of this pointer (RFC 6901 §6): <c>#</c> followed by the pointer
    /// with every character that a URI fragment cannot carry percent-encoded as UTF-8.
    /// </summary>
    /// <returns>The fragment, including its leading <c>#</c>.</returns>
    /// <exception cref="InvalidOperationException">The pointer is <see langword="default"/>.</exception>
    public string ToUriFragment()
    {
        return "#" + Internal.UriReference.EscapeFragment(RequireValue());
    }

    /// <summary>
    /// Parses the URI fragment form of a pointer (RFC 6901 §6).
    /// </summary>
    /// <param name="fragment">The fragment, with or without its leading <c>#</c>.</param>
    /// <param name="pointer">The pointer when parsing succeeds; otherwise <see langword="default"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the fragment percent-decodes to valid UTF-8 that is a valid
    /// pointer.
    /// </returns>
    public static bool TryParseUriFragment(string? fragment, out JsonPointer pointer)
    {
        pointer = default;
        if (fragment is null)
        {
            return false;
        }

        ReadOnlySpan<char> text = fragment.AsSpan();
        if (text.Length > 0 && text[0] == '#')
        {
            text = text.Slice(1);
        }

        string? decoded = Internal.UriReference.PercentDecode(text);
        return decoded is not null && TryCreate(decoded, out pointer);
    }

    internal static JsonPointer CreateUnchecked(string value) => new(value, validated: true);

    internal static string EscapeToken(string token)
    {
        if (token.AsSpan().IndexOfAny('~', '/') < 0)
        {
            return token;
        }

        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    internal static string UnescapeToken(ReadOnlySpan<char> token)
    {
        if (token.IndexOf('~') < 0)
        {
            return token.ToString();
        }

        StringBuilder builder = new(token.Length);
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c == '~' && i + 1 < token.Length)
            {
                builder.Append(token[i + 1] == '0' ? '~' : '/');
                i++;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool TryStep(ref JsonElement current, ReadOnlySpan<char> rawToken)
    {
        switch (current.ValueKind)
        {
            case JsonValueKind.Object:
                return current.TryGetProperty(UnescapeToken(rawToken), out current);
            case JsonValueKind.Array:
                if (!TryParseIndex(rawToken, out int index) || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseIndex(ReadOnlySpan<char> token, out int index)
    {
        index = 0;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0'))
        {
            return false;
        }

        foreach (char c in token)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    [MemberNotNull(nameof(Value))]
    private string RequireValue()
    {
        if (Value is null)
        {
            throw new InvalidOperationException(ErrorMessages.JsonPointerDefault);
        }

        return Value;
    }
}
