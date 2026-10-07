using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Splits an HTTP/1.1 field line into its name and value, for the header section and for a chunked
/// trailer section alike: <c>field-line = field-name ":" OWS field-value OWS</c> (RFC 9112 §5.1),
/// where the field name is a token (RFC 9110 §5.1).
/// </summary>
/// <remarks>
/// The name is taken as it stands, never trimmed. A server MUST reject a request with whitespace
/// between a field name and its colon (RFC 9112 §5.1): an intermediary that strips the whitespace and
/// one that keeps it would disagree about the message, which is how requests are smuggled (#1333).
/// Requiring a token also rules out an empty name and a line that starts with whitespace, the
/// obsolete line folding that RFC 9112 §5.2 lets a server reject.
/// </remarks>
internal static class Http1FieldLine
{
    // RFC 9110 §5.6.2 — tchar, the characters of a token.
    private static readonly SearchValues<char> _tokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Splits <paramref name="line"/> at its first colon into a field name and a field value with the
    /// optional whitespace around it removed.
    /// </summary>
    /// <param name="line">The field line, without its CRLF.</param>
    /// <param name="name">The field name, when the line is well formed.</param>
    /// <param name="value">The field value, when the line is well formed.</param>
    /// <returns>
    /// <see langword="false"/> when the line has no colon or the text before it is not a token: empty,
    /// or holding whitespace or any other character a token cannot carry.
    /// </returns>
    public static bool TryParse(string line, out string name, out string value)
    {
        int colon = line.IndexOf(':');

        if (colon <= 0 || line.AsSpan(0, colon).ContainsAnyExcept(_tokenChars))
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }

        name = line[..colon];
        value = line[(colon + 1)..].Trim();
        return true;
    }
}
