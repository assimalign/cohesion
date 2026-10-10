using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Splits an HTTP/1.1 field line into its name and value, for the header section and for a chunked
/// trailer section alike: <c>field-line = field-name ":" OWS field-value OWS</c> (RFC 9112 §5.1),
/// where the field name is a token (RFC 9110 §5.1) and the field value carries no control character
/// but HTAB (RFC 9110 §5.5).
/// </summary>
/// <remarks>
/// <para>
/// The name is taken as it stands, never trimmed. A server MUST reject a request with whitespace
/// between a field name and its colon (RFC 9112 §5.1): an intermediary that strips the whitespace and
/// one that keeps it would disagree about the message, which is how requests are smuggled (#1333).
/// Requiring a token also rules out an empty name and a line that starts with whitespace, the
/// obsolete line folding that RFC 9112 §5.2 lets a server reject.
/// </para>
/// <para>
/// The value loses its optional whitespace, SP and HTAB only (RFC 9110 §5.6.3); any other character
/// at either end, a no-break space or a vertical tab among them, belongs to the value (#1341). A
/// value that then holds NUL, CR, LF, or any other control character but HTAB is rejected
/// (RFC 9110 §5.5): the head's line reader ends a line only at CRLF, so a bare CR or LF arrives here
/// in a header line, and an intermediary that ends the line at it would read a different field. A
/// trailer line never carries one this far: the chunk framing line reader refuses a bare CR or LF
/// itself (#1375), so for a trailer this check meets the other control characters only. Both halves
/// come from the core field rule (<see cref="HttpFieldNormalization"/>), which the chunk extensions
/// share and the response writers (#1183) and the HTTP/2 and HTTP/3 decoders (#1376) are to share.
/// The line is decoded as Latin-1, so an obs-text octet (<c>%x80-FF</c>) reaches the value intact.
/// </para>
/// </remarks>
internal static class Http1FieldLine
{
    /// <summary>
    /// Optional whitespace, SP and HTAB (RFC 9110 §5.6.3): the only characters trimmed from a field
    /// value or from a list element inside one. <c>string.Trim()</c> and
    /// <see cref="StringSplitOptions.TrimEntries"/> also strip every Unicode white-space character, a
    /// no-break space (<c>0xA0</c>) and a next-line octet (<c>0x85</c>) among them, which reach a
    /// value as obs-text because lines are decoded as Latin-1.
    /// </summary>
    public const string OptionalWhitespace = " \t";

    /// <summary>
    /// Splits <paramref name="line"/> at its first colon into a field name and a field value with the
    /// optional whitespace around it removed.
    /// </summary>
    /// <param name="line">The field line, without its CRLF.</param>
    /// <param name="name">The field name, when the line is well formed.</param>
    /// <param name="value">The field value, when the line is well formed.</param>
    /// <param name="violation">
    /// When the line is malformed, the rule it breaks. The description never quotes the value: a
    /// malformed one can carry CR, LF, or NUL, which a log would otherwise receive verbatim.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the line has no colon, the text before it is not a token (empty,
    /// or holding whitespace or any other character a token cannot carry), or the value holds a
    /// control character other than HTAB.
    /// </returns>
    public static bool TryParse(string line, out string name, out string value, [NotNullWhen(false)] out string? violation)
    {
        name = string.Empty;
        value = string.Empty;

        int colon = line.IndexOf(':');

        if (colon <= 0 || !HttpFieldNormalization.IsValidFieldName(line.AsSpan(0, colon)))
        {
            violation = "RFC 9112 §5.1: a field line has no colon, or a field name that is not a token.";
            return false;
        }

        // RFC 9112 §5.1 / RFC 9110 §5.6.3 — OWS is SP and HTAB. string.Trim() would also strip a
        // vertical tab, a form feed, a bare CR, and a no-break space, all of which another parser keeps.
        ReadOnlySpan<char> fieldValue = line.AsSpan(colon + 1).Trim(OptionalWhitespace);

        int invalid = HttpFieldNormalization.IndexOfInvalidControlCharacter(fieldValue);
        if (invalid >= 0)
        {
            // The name is a token, so it is safe to quote.
            violation = $"RFC 9110 §5.5: the value of the field '{line[..colon]}' holds the control character 0x{(int)fieldValue[invalid]:X2}; only HTAB is allowed.";
            return false;
        }

        name = line[..colon];
        value = fieldValue.ToString();
        violation = null;
        return true;
    }
}
