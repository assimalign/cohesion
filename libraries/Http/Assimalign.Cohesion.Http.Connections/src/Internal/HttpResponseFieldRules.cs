using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The rules every response head writer applies to the fields it encodes: the field syntax every version
/// shares, and, for HTTP/2 and HTTP/3, the connection-specific fields their heads never carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Field syntax (#1183, decision 27).</b> <see cref="EnsureValidName"/> and
/// <see cref="EnsureValidValue"/> apply the core field rule (<see cref="HttpFieldNormalization"/>) to each
/// field line as it is encoded: the name is a token (RFC 9110 §5.1), and the value holds no control
/// character other than HTAB (RFC 9110 §5.5). CR and LF end an HTTP/1.1 field line, so a value that
/// carries them splits the response, and RFC 9113 §8.2.1 and RFC 9114 §4.2 make an HTTP/2 or HTTP/3
/// field holding CR, LF, or NUL malformed. The other control characters are refused as well: a sender
/// MUST NOT generate a field value outside the <c>field-content</c> grammar (RFC 9110 §2.2), and the
/// tolerance §5.5 grants is a recipient's. The HTTP/1.1 reader rejects the same characters (#1341). A
/// value is not judged for SP or HTAB at its ends, which split nothing and which an HTTP/1.1 recipient
/// strips, nor for a character above <c>U+00FF</c>, which every encoder writes as <c>?</c>.
/// </para>
/// <para>
/// The check runs at encode time, in each writer, because a check in the header collection alone can be
/// bypassed: <see cref="IHttpHeaderCollection"/> is an interface anyone can implement, and a
/// <see cref="HttpHeaderValue"/> built over an array shares that array with its caller. A refused field
/// throws <see cref="HttpInvalidResponseFieldException"/> before any byte of the head is written.
/// </para>
/// <para>
/// <b>Connection-specific fields.</b> RFC 9113 §8.2.2 and RFC 9114 §4.2 make a message that carries a
/// connection-specific field (<c>Connection</c>, <c>Keep-Alive</c>, <c>Proxy-Connection</c>,
/// <c>Transfer-Encoding</c>, <c>Upgrade</c>) malformed, and allow <c>TE</c> only with the value
/// <c>trailers</c>; a client may reset the stream that carries one. Applications, middleware written for
/// HTTP/1.1, and proxies set these fields, so the HTTP/2 and HTTP/3 encoders skip them while they encode a
/// response head: buffered, streamed, interim (early hints), and a tunnel's <c>200</c> (#1328). The
/// application's header collection is left as it was; only the wire loses the field. A trailer section
/// needs no such filter: <see cref="HttpTrailerFieldRules"/> rejects these fields when the application
/// stages them.
/// </para>
/// </remarks>
internal static class HttpResponseFieldRules
{
    /// <summary>
    /// Determines whether a response field may be encoded into an HTTP/2 or HTTP/3 response head.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>
    /// <see langword="false"/> for a connection-specific field, and for <c>TE</c> with any value other
    /// than <c>trailers</c>; otherwise <see langword="true"/>.
    /// </returns>
    public static bool IsSendable(HttpHeaderKey key, HttpHeaderValue value)
    {
        if (HttpFieldNormalization.IsForbiddenInHttp2Or3(key))
        {
            return false;
        }

        // An empty TE says nothing and a strict client rejects any value but "trailers", so only that
        // value goes out.
        return key != HttpHeaderKey.TE || string.Equals(value.Value, "trailers", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuses a response field name that is not a token (RFC 9110 §5.1).
    /// </summary>
    /// <param name="key">The field name about to be encoded.</param>
    /// <exception cref="HttpInvalidResponseFieldException">The name is not a token.</exception>
    public static void EnsureValidName(HttpHeaderKey key)
    {
        string name = key.Value ?? string.Empty;

        if (!HttpFieldNormalization.IsValidFieldName(name))
        {
            throw new HttpInvalidResponseFieldException(DescribeInvalidName(name));
        }
    }

    /// <summary>
    /// Refuses a response field value that holds a control character other than HTAB: CR, LF, NUL, or
    /// any other CTL (RFC 9110 §5.5).
    /// </summary>
    /// <param name="key">The field name, already checked by <see cref="EnsureValidName"/>.</param>
    /// <param name="value">The value exactly as it is about to be encoded.</param>
    /// <exception cref="HttpInvalidResponseFieldException">The value holds such a character.</exception>
    public static void EnsureValidValue(HttpHeaderKey key, ReadOnlySpan<char> value)
    {
        int invalid = HttpFieldNormalization.IndexOfInvalidControlCharacter(value);

        if (invalid >= 0)
        {
            // The name passed EnsureValidName, so it is a token and safe to quote; the value is not.
            throw new HttpInvalidResponseFieldException(
                $"RFC 9110 §5.5: the value of the response field '{key.Value}' holds the control character 0x{(int)value[invalid]:X2} at index {invalid}; a field value holds no control character but HTAB. The response head was not sent.");
        }
    }

    private static string DescribeInvalidName(string name)
    {
        if (name.Length == 0)
        {
            return "RFC 9110 §5.1: a response field name is empty. The response head was not sent.";
        }

        int index = 0;

        while (index < name.Length && HttpFieldNormalization.IsValidFieldName(name.AsSpan(index, 1)))
        {
            index++;
        }

        // The name is not quoted: whatever made it invalid may be CR, LF, or NUL.
        return $"RFC 9110 §5.1: a response field name holds the character 0x{(int)name[index]:X2} at index {index}, which a token cannot carry. The response head was not sent.";
    }
}
