using System;
using System.Buffers;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The field syntax HTTP/2 and HTTP/3 apply to every field line they receive, in a request head and in
/// a trailer section alike (#1376): the core field rule (<see cref="HttpFieldNormalization"/>, #1341)
/// with the exclusions RFC 9113 §8.2.1 and RFC 9114 §4.2 add for these versions.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name</b> is a token (RFC 9110 §5.1) and holds no uppercase letter (RFC 9113 §8.2.1, RFC 9114
/// §4.2). A token already excludes <c>:</c>, SP, every control character, and anything outside VCHAR, so
/// a name that starts with <c>:</c> is never a regular field name: the decoders route it to the
/// pseudo-header rules first, and a trailer section carries none.
/// </para>
/// <para>
/// <b>A value</b>, a pseudo-header's included, has no NUL, CR, or LF and no SP or HTAB at either end
/// (<see cref="HttpFieldNormalization.IsValidFieldValue"/>, RFC 9113 §8.2.1, RFC 9114 §4.2, §10.3), and
/// no other control character but HTAB (<see cref="HttpFieldNormalization.IndexOfInvalidControlCharacter"/>,
/// RFC 9110 §5.5). RFC 9110 lets a recipient keep those other control characters, but the HTTP/1.1
/// reader refuses them (#1341) and every response writer refuses to send them (#1183): a request that
/// reached the application with one would turn an application that reflects it into a <c>500</c>
/// rather than a malformed request refused at ingress, and would differ by version.
/// </para>
/// <para>
/// A violation throws <see cref="InvalidDataException"/>: the request is malformed, and each version's
/// receive path turns that into a stream error that costs the request alone — <c>PROTOCOL_ERROR</c> on
/// HTTP/2 (RFC 9113 §8.1.1), <c>H3_MESSAGE_ERROR</c> on HTTP/3 (RFC 9114 §4.1.2). A message never quotes
/// text that may still hold a control character: an invalid name is described by its offending
/// character, and a value is never quoted.
/// </para>
/// <para>
/// <b>Cost.</b> The rule runs on every field line of every request head and trailer section, so an
/// accepted field costs two vectorized scans: one over the name against the lowercase <c>tchar</c> set,
/// and one over the value for a control character, after which only its two end characters are read.
/// The slower checks run only to describe a refusal.
/// </para>
/// </remarks>
internal static class HttpReceivedFieldRules
{
    // RFC 9110 §5.6.2 tchar without 'A'-'Z': what a received HTTP/2 or HTTP/3 field name may hold, since
    // RFC 9113 §8.2.1 and RFC 9114 §4.2 require it lowercase. Every field line of every head and trailer
    // section is checked, so a valid name costs one scan; the core rule
    // (HttpFieldNormalization.IsValidFieldName) runs only to describe a name this set refuses.
    private static readonly SearchValues<char> _lowercaseTokenCharacters = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Rejects a received regular field name that is not a lowercase token.
    /// </summary>
    /// <param name="name">The decoded field name, which does not start with <c>:</c>.</param>
    /// <param name="protocol">The protocol named in the message: <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
    /// <exception cref="InvalidDataException">The name is empty, not a token, or holds an uppercase letter.</exception>
    public static void EnsureValidName(string name, string protocol)
    {
        if (name.Length == 0)
        {
            throw new InvalidDataException($"The {protocol} field section contains a zero-length field name.");
        }

        if (!name.AsSpan().ContainsAnyExcept(_lowercaseTokenCharacters))
        {
            return;
        }

        if (!HttpFieldNormalization.IsValidFieldName(name))
        {
            int index = IndexOfNonTokenCharacter(name);
            throw new InvalidDataException(
                $"An {protocol} field name holds the character 0x{(int)name[index]:X2} at index {index}, which a token cannot carry (RFC 9110 §5.1, RFC 9113 §8.2.1, RFC 9114 §4.2).");
        }

        // A token that is not a lowercase token holds an uppercase letter, and is safe to quote.
        throw new InvalidDataException(
            $"The {protocol} field name '{name}' must be lowercase (RFC 9113 §8.2.1, RFC 9114 §4.2).");
    }

    /// <summary>
    /// Rejects a received field value — of a regular field or a pseudo-header — that holds NUL, CR, LF, or
    /// another control character but HTAB, or that starts or ends with SP or HTAB.
    /// </summary>
    /// <param name="name">The field name, quoted in the message only when it is safe to.</param>
    /// <param name="value">The decoded field value.</param>
    /// <param name="protocol">The protocol named in the message: <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
    /// <exception cref="InvalidDataException">The value breaks the rule.</exception>
    public static void EnsureValidValue(string name, string value, string protocol)
    {
        int invalid = HttpFieldNormalization.IndexOfInvalidControlCharacter(value);

        if (invalid >= 0)
        {
            throw new InvalidDataException(
                $"The value of the {protocol} field {DescribeName(name)} holds the control character 0x{(int)value[invalid]:X2} at index {invalid}; a field value holds no control character but HTAB (RFC 9110 §5.5, RFC 9113 §8.2.1, RFC 9114 §4.2).");
        }

        // The control characters include NUL, CR, and LF, so of IsValidFieldValue's rule only the ends are
        // left to check, and the value is not scanned again.
        if (value.Length > 0 && (IsOptionalWhitespace(value[0]) || IsOptionalWhitespace(value[^1])))
        {
            throw new InvalidDataException(
                $"The value of the {protocol} field {DescribeName(name)} starts or ends with whitespace (RFC 9113 §8.2.1, RFC 9114 §4.2).");
        }
    }

    /// <summary>
    /// Describes a received field name for a message: quoted when it, or a pseudo-header's name after its
    /// colon, is a token, and otherwise described without its text, which may hold CR, LF, or NUL.
    /// </summary>
    /// <param name="name">The received field name.</param>
    /// <returns>The quoted name, or a description of it.</returns>
    public static string DescribeName(string name)
    {
        ReadOnlySpan<char> token = name.Length > 1 && name[0] == ':' ? name.AsSpan(1) : name;

        return HttpFieldNormalization.IsValidFieldName(token) ? $"'{name}'" : "with a name that is not a token";
    }

    private static bool IsOptionalWhitespace(char character)
    {
        // RFC 9110 §5.6.3 — OWS is SP / HTAB, nothing else.
        return character is ' ' or '\t';
    }

    private static int IndexOfNonTokenCharacter(string name)
    {
        int index = 0;

        while (index < name.Length - 1 && HttpFieldNormalization.IsValidFieldName(name.AsSpan(index, 1)))
        {
            index++;
        }

        return index;
    }
}
