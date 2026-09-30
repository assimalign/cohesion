using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;


namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Bridges QPACK field sections (RFC 9204) to the HTTP message model and
/// enforces the HTTP/3 field-section rules (RFC 9114 §4.2 / §4.3): the
/// pseudo-header set, pseudo-before-regular ordering, lowercase field
/// names, connection-specific field prohibition, required request
/// pseudo-headers, and — for a trailer section — the absence of
/// pseudo-headers and framing fields. Decoding the QPACK representation
/// itself (static table, or the opt-in dynamic table) is the connection
/// context's job; this codec validates the decoded field lines. Response
/// field sections are encoded against the static table and literals only.
/// </summary>
internal static class Http3HeaderCodec
{
    /// <summary>
    /// Applies the HTTP/3 field-section rules (RFC 9114 §4.2 / §4.3) to an
    /// already-decoded request header section and builds the request head. The
    /// connection context decodes the QPACK field lines (static-only or against
    /// the dynamic table) and hands them here, so both paths validate identically.
    /// The request body is not part of the head: the returned request carries a
    /// placeholder body the caller replaces with the lazily read request-body
    /// stream.
    /// </summary>
    /// <param name="fields">The decoded name/value field lines, in wire order.</param>
    /// <param name="fallbackScheme">The scheme to use when no <c>:scheme</c> is present.</param>
    /// <param name="trailers">The trailer collection the request surfaces, filled when a trailer section arrives.</param>
    /// <param name="extendedConnectProtocol">The <c>:protocol</c> pseudo-header value, when present.</param>
    /// <param name="contentLength">The declared <c>Content-Length</c>, or <see langword="null"/> when absent.</param>
    /// <returns>The validated HTTP/3 request.</returns>
    /// <exception cref="InvalidDataException">Thrown when the field section violates an HTTP/3 message rule.</exception>
    public static Http3Request BuildRequest(
        List<(string Name, string Value)> fields,
        HttpScheme fallbackScheme,
        HttpTrailerCollection trailers,
        out string? extendedConnectProtocol,
        out long? contentLength)
    {
        HttpHeaderCollection headers = new();
        string? authority = null;
        string? method = null;
        string? pathValue = null;
        string? schemeValue = null;
        string? protocol = null;
        bool seenRegularField = false;

        foreach ((string name, string value) in fields)
        {
            if (name.Length == 0)
            {
                throw new InvalidDataException("HTTP/3 field section contains a zero-length field name.");
            }

            if (name[0] == ':')
            {
                // RFC 9114 §4.3 — all pseudo-header fields MUST precede the
                // regular fields.
                if (seenRegularField)
                {
                    throw new InvalidDataException("HTTP/3 pseudo-header field appears after a regular field (RFC 9114 §4.3).");
                }

                switch (name)
                {
                    case ":method":
                        AssignOncePseudoHeader(ref method, value, name);
                        break;
                    case ":scheme":
                        AssignOncePseudoHeader(ref schemeValue, value, name);
                        break;
                    case ":authority":
                        AssignOncePseudoHeader(ref authority, value, name);
                        break;
                    case ":path":
                        AssignOncePseudoHeader(ref pathValue, value, name);
                        break;
                    case ":protocol":
                        // RFC 8441 / RFC 9220 extended CONNECT indicator;
                        // recognized so it is not rejected as unknown, and
                        // surfaced verbatim (see extendedConnectProtocol) for a
                        // higher layer to model. The transport does not interpret it.
                        AssignOncePseudoHeader(ref protocol, value, name);
                        break;
                    default:
                        throw new InvalidDataException($"HTTP/3 request contains an unknown pseudo-header field '{name}' (RFC 9114 §4.3.1).");
                }

                continue;
            }

            seenRegularField = true;

            // RFC 9114 §4.2 — field names MUST be lowercase; an uppercase
            // character makes the request malformed.
            if (!IsLowercaseFieldName(name))
            {
                throw new InvalidDataException($"HTTP/3 field name '{name}' must be lowercase (RFC 9114 §4.2).");
            }

            HttpHeaderKey key = new(name);

            // RFC 9114 §4.2 — connection-specific fields are forbidden in
            // HTTP/3 (the same set as HTTP/2), and TE may only be "trailers".
            // The rule is shared with HTTP/2 via HttpFieldNormalization. A
            // malformed field section is rejected; the receive loop resets the
            // offending stream without tearing down the connection.
            if (HttpFieldNormalization.IsForbiddenInHttp2Or3(key))
            {
                throw new InvalidDataException(
                    $"Connection-specific header field '{name}' is forbidden in HTTP/3 (RFC 9114 §4.2).");
            }

            if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase)
                && !HttpFieldNormalization.IsTeValueValidInHttp2Or3(value))
            {
                throw new InvalidDataException(
                    $"HTTP/3 field 'TE' MUST carry only the value 'trailers'; got '{value}'.");
            }

            if (headers.TryGetValue(key, out HttpHeaderValue existingValue))
            {
                // RFC 9114 §4.2.1 — repeated-field combining (Cookie coalesces
                // with "; ", other list fields combine) matches HTTP/2.
                headers[key] = HttpFieldNormalization.CombineFieldValue(key, existingValue, value);
            }
            else
            {
                headers[key] = value;
            }
        }

        // RFC 9114 §4.3.1 — required request pseudo-headers. A CONNECT request
        // omits :scheme and :path; all other methods MUST include exactly one
        // of each.
        if (method is null)
        {
            throw new InvalidDataException("HTTP/3 request is missing the :method pseudo-header (RFC 9114 §4.3.1).");
        }

        bool isConnect = string.Equals(method, "CONNECT", StringComparison.Ordinal);

        if (!isConnect)
        {
            if (schemeValue is null)
            {
                throw new InvalidDataException("HTTP/3 request is missing the :scheme pseudo-header (RFC 9114 §4.3.1).");
            }

            if (string.IsNullOrEmpty(pathValue))
            {
                throw new InvalidDataException("HTTP/3 request is missing a non-empty :path pseudo-header (RFC 9114 §4.3.1).");
            }
        }

        // RFC 8441 §4 / RFC 9220 §3 — validate extended CONNECT (the :protocol
        // pseudo-header): it is only valid on a CONNECT, and an extended CONNECT
        // MUST also carry :scheme, :path, and :authority. A violation is a
        // malformed request (RFC 9114 §4.1.2); the receive loop drops the
        // offending stream without tearing down the connection. The cross-field
        // rule is shared with HTTP/2 via HttpFieldNormalization.
        string? extendedConnectViolation = HttpFieldNormalization.ValidateExtendedConnect(
            method, schemeValue, pathValue, authority, protocol);
        if (extendedConnectViolation is not null)
        {
            throw new InvalidDataException(extendedConnectViolation);
        }

        HttpQueryCollection query = ParseQuery(pathValue ?? "/", out HttpPath path);
        // RFC 9114 §4.3.1 — :authority supersedes Host, resolved identically to
        // HTTP/2 via HttpFieldNormalization.
        HttpHost host = HttpFieldNormalization.ResolveAuthority(authority, headers);
        HttpScheme scheme = schemeValue is null
            ? fallbackScheme
            : string.Equals(schemeValue, "https", StringComparison.OrdinalIgnoreCase) ? HttpScheme.Https : HttpScheme.Http;

        // Surface the raw :protocol pseudo-header (RFC 8441 / RFC 9220) so the
        // connection context can stash it generically for a higher layer; the
        // transport itself does not interpret extended CONNECT.
        extendedConnectProtocol = protocol;

        // RFC 9114 §4.1.2 — the request body is checked against a declared
        // Content-Length as its DATA frames arrive; a value that is not a valid
        // length makes the request malformed here, before it is dispatched.
        contentLength = ParseContentLength(headers);

        return new Http3Request(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(method),
            scheme,
            query,
            headers,
            Stream.Null,
            trailers);
    }

    /// <summary>
    /// Validates a decoded request trailer section (RFC 9114 §4.1) and adds its fields to
    /// <paramref name="trailers"/>. A trailer section carries no pseudo-header fields
    /// (RFC 9114 §4.3), follows the same field-name rules as a header section (RFC 9114 §4.2),
    /// and may not carry the fields that frame or route a message — <c>Content-Length</c> and
    /// <c>Host</c> — which a trailer could otherwise use to contradict the head it follows
    /// (RFC 9110 §6.5.1; the HTTP/1.1 reader rejects the same set).
    /// </summary>
    /// <param name="fields">The decoded name/value field lines, in wire order.</param>
    /// <param name="trailers">The request's trailer collection.</param>
    /// <exception cref="InvalidDataException">Thrown when the trailer section violates an HTTP/3 message rule.</exception>
    public static void AddTrailers(List<(string Name, string Value)> fields, HttpTrailerCollection trailers)
    {
        foreach ((string name, string value) in fields)
        {
            if (name.Length == 0)
            {
                throw new InvalidDataException("HTTP/3 trailer section contains a zero-length field name.");
            }

            if (name[0] == ':')
            {
                throw new InvalidDataException($"HTTP/3 trailer section contains the pseudo-header field '{name}' (RFC 9114 §4.3).");
            }

            if (!IsLowercaseFieldName(name))
            {
                throw new InvalidDataException($"HTTP/3 trailer field name '{name}' must be lowercase (RFC 9114 §4.2).");
            }

            HttpHeaderKey key = new(name);

            if (HttpFieldNormalization.IsForbiddenInHttp2Or3(key)
                || key.Equals(HttpHeaderKey.ContentLength)
                || key.Equals(HttpHeaderKey.Host))
            {
                throw new InvalidDataException($"HTTP/3 trailer field '{name}' is not permitted in a trailer section (RFC 9114 §4.2, RFC 9110 §6.5.1).");
            }

            if (trailers.TryGetValue(key, out HttpHeaderValue existingValue))
            {
                trailers[key] = HttpFieldNormalization.CombineFieldValue(key, existingValue, value);
            }
            else
            {
                trailers[key] = value;
            }
        }
    }

    /// <summary>
    /// Encodes the field section of a bodyless final response the transport answers on its own —
    /// the <c>:status</c> pseudo-header and <c>content-length: 0</c> — used when a request is
    /// refused before it ever became an exchange (a request-body limit violated while a request
    /// interceptor read the body).
    /// </summary>
    /// <param name="statusCode">The final status code.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    public static byte[] EncodeStatusOnlyResponseHeaders(HttpStatusCode statusCode)
    {
        List<(string Name, string Value)> fields =
        [
            (":status", ((int)statusCode).ToString(CultureInfo.InvariantCulture)),
            ("content-length", "0"),
        ];

        return QPackFieldSectionEncoder.Encode(fields);
    }

    /// <summary>
    /// Encodes the field section for an <em>interim</em> (<c>1xx</c>) response: the <c>:status</c>
    /// pseudo-header set to the interim code followed by the supplied fields verbatim, with <b>no</b>
    /// <c>Content-Length</c> (an interim response carries no body — RFC 9110 §15.2). Written as an
    /// additional HEADERS frame on the request stream, before the final HEADERS frame (RFC 9114 §4.1).
    /// </summary>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    public static byte[] EncodeInterimResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection? headers)
    {
        List<(string Name, string Value)> fields =
        [
            (":status", ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        if (headers is not null)
        {
            foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        fields.Add((header.Key.Value.ToLowerInvariant(), value));
                    }
                }
            }
        }

        return QPackFieldSectionEncoder.Encode(fields);
    }

    public static byte[] EncodeResponseHeaders(Http3Context context, byte[] bodyBytes)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return EncodeResponseHeaders(context);
    }

    /// <summary>
    /// Encodes the response field section for an <em>incrementally streamed</em>
    /// response: the <c>:status</c> pseudo-header followed by the response headers
    /// verbatim, with <b>no</b> synthesized <c>Content-Length</c>. HTTP/3 delimits
    /// the body with the stream end, so a length is neither known up front nor
    /// required.
    /// </summary>
    /// <param name="context">The exchange whose response head is encoded.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    public static byte[] EncodeResponseHeaders(Http3Context context)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        List<(string Name, string Value)> fields =
        [
            (":status", ((int)context.Response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-folded value is
            // forbidden.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        fields.Add(("set-cookie", value));
                    }
                }
            }
            else
            {
                fields.Add((header.Key.Value, header.Value.Value));
            }
        }

        return QPackFieldSectionEncoder.Encode(fields);
    }

    private static void AssignOncePseudoHeader(ref string? slot, string value, string name)
    {
        if (slot is not null)
        {
            // RFC 9114 §4.3.1 — a pseudo-header field MUST NOT appear more
            // than once.
            throw new InvalidDataException($"HTTP/3 request contains a duplicate pseudo-header field '{name}' (RFC 9114 §4.3.1).");
        }

        slot = value;
    }

    private static bool IsLowercaseFieldName(string name)
    {
        foreach (char c in name)
        {
            if (c is >= 'A' and <= 'Z')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses the request's <c>Content-Length</c>, if any (RFC 9110 §8.6): one or more identical
    /// non-negative decimal values — as separate field lines or a comma-separated list — or the
    /// request is malformed (RFC 9114 §4.1.2).
    /// </summary>
    private static long? ParseContentLength(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue raw))
        {
            return null;
        }

        long? agreed = null;

        foreach (string? entry in raw)
        {
            if (entry is null)
            {
                continue;
            }

            foreach (string segment in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsAsciiDigits(segment)
                    || !long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    throw new InvalidDataException($"HTTP/3 request Content-Length value '{segment}' is not a non-negative decimal integer (RFC 9110 §8.6).");
                }

                if (agreed is { } existing && existing != parsed)
                {
                    throw new InvalidDataException($"HTTP/3 request declares conflicting Content-Length values ({existing} and {parsed}) (RFC 9110 §8.6).");
                }

                agreed = parsed;
            }
        }

        return agreed ?? throw new InvalidDataException("HTTP/3 request carries an empty Content-Length field (RFC 9110 §8.6).");
    }

    private static bool IsAsciiDigits(string value)
    {
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static HttpQueryCollection ParseQuery(string requestTarget, out HttpPath path)
    {
        int queryIndex = requestTarget.IndexOf('?');

        if (queryIndex >= 0)
        {
            path = HttpPath.FromUriComponent(requestTarget[..queryIndex]);
            return new HttpQuery(requestTarget[(queryIndex + 1)..]).Parse();
        }

        path = HttpPath.FromUriComponent(requestTarget);
        return new HttpQueryCollection();
    }
}
