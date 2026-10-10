using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;


namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Bridges QPACK field sections (RFC 9204) to the HTTP message model and
/// enforces the HTTP/3 field-section rules (RFC 9114 §4.2 / §4.3): the
/// pseudo-header set, pseudo-before-regular ordering, the field syntax
/// (lowercase token names and values without control characters,
/// <see cref="HttpReceivedFieldRules"/>, #1376), connection-specific field
/// prohibition, required request
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
    /// The request body is not part of the field section: the returned head carries
    /// a placeholder body the caller replaces with the lazily read request-body
    /// stream. The head carries the <c>:protocol</c> of a validated extended CONNECT
    /// (<see cref="TransportHttpRequestHead.Protocol"/>), <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="fields">The decoded name/value field lines, in wire order.</param>
    /// <param name="fallbackScheme">The scheme to use when no <c>:scheme</c> is present.</param>
    /// <param name="trailers">The trailer collection the request surfaces, filled when a trailer section arrives.</param>
    /// <param name="contentLength">The declared <c>Content-Length</c>, or <see langword="null"/> when absent.</param>
    /// <returns>The validated HTTP/3 request head.</returns>
    /// <exception cref="InvalidDataException">Thrown when the field section violates an HTTP/3 message rule.</exception>
    public static TransportHttpRequestHead BuildRequestHead(
        List<(string Name, string Value)> fields,
        HttpScheme fallbackScheme,
        HttpTrailerCollection trailers,
        out long? contentLength)
    {
        HttpHeaderCollection headers = new();
        string? authority = null;
        string? method = null;
        string? pathValue = null;
        string? schemeValue = null;
        string? protocol = null;
        bool seenRegularField = false;
        // RFC 9114 §4.2.1 — the crumbs of a split cookie field, joined with "; " once the section is read.
        HttpCookieCrumbs cookieCrumbs = default;

        foreach ((string name, string value) in fields)
        {
            if (name.Length == 0)
            {
                throw new InvalidDataException("HTTP/3 field section contains a zero-length field name.");
            }

            // RFC 9114 §4.2 / RFC 9113 §8.2.1 — a value, a pseudo-header's included, holds no NUL, CR,
            // or LF and no whitespace at either end; the core rule also refuses the other control
            // characters (#1376). The receive loop resets the stream with H3_MESSAGE_ERROR.
            HttpReceivedFieldRules.EnsureValidValue(name, value, "HTTP/3");

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
                        // RFC 8441 / RFC 9220 extended CONNECT indicator; validated
                        // against the method and the other pseudo-headers below, then
                        // carried on the head for the request-parse interceptors and
                        // the exchange's tunnel accept.
                        AssignOncePseudoHeader(ref protocol, value, name);
                        break;
                    default:
                        throw new InvalidDataException($"HTTP/3 request contains an unknown pseudo-header field {HttpReceivedFieldRules.DescribeName(name)} (RFC 9114 §4.3.1).");
                }

                continue;
            }

            seenRegularField = true;

            // RFC 9114 §4.2 — a field name is a lowercase token, so it holds no ':', SP, control
            // character, or uppercase letter; any of them makes the request malformed (#1376).
            HttpReceivedFieldRules.EnsureValidName(name, "HTTP/3");

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
                // with "; ", other list fields combine) is HTTP/2's rule, through the
                // same two helpers. Both run in time linear in the repeats:
                // HttpHeaderValue.Concat appends in amortized constant time, and
                // cookie crumbs are joined once below.
                if (!cookieCrumbs.TryAdd(key, existingValue, value))
                {
                    headers[key] = HttpFieldNormalization.CombineFieldValue(key, existingValue, value);
                }
            }
            else
            {
                headers[key] = value;
            }
        }

        cookieCrumbs.Join(headers);

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
        // pseudo-header): a present :protocol must name a protocol (RFC 9110
        // §5.6.2, so never empty), it is only valid on a CONNECT, and an extended
        // CONNECT MUST also carry :scheme, :path, and :authority. A violation is a
        // malformed request (RFC 9114 §4.1.2): the caller resets the offending
        // stream with H3_MESSAGE_ERROR without tearing down the connection, as
        // HTTP/2 resets its stream with PROTOCOL_ERROR. The cross-field rule is
        // shared with HTTP/2 via HttpFieldNormalization.
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

        // RFC 9114 §4.1.2 — the request body is checked against a declared
        // Content-Length as its DATA frames arrive; a value that is not a valid
        // length makes the request malformed here, before it is dispatched.
        contentLength = ParseContentLength(headers);

        // RFC 8441 §4 / RFC 9220 §3 — the head carries :protocol only once it passed the extended
        // CONNECT validation above, which rejects an empty value and any method but CONNECT, so a
        // non-null value always means a valid extended CONNECT with a non-empty protocol name.
        return new TransportHttpRequestHead(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(method),
            scheme,
            query,
            headers,
            Stream.Null,
            trailers,
            protocol);
    }

    /// <summary>
    /// Validates a decoded request trailer section (RFC 9114 §4.1) and adds its fields to
    /// <paramref name="trailers"/>. The rules are the ones HTTP/2 applies
    /// (<see cref="HttpTrailerFieldRules.AddReceivedFields"/>): no pseudo-header field (RFC 9114 §4.3),
    /// the header section's field-name rules (RFC 9114 §4.2), and none of the fields RFC 9110 §6.5.1
    /// excludes from trailers, among them the <c>Content-Length</c> and <c>Host</c> a trailer could
    /// otherwise use to contradict the head it follows.
    /// </summary>
    /// <param name="fields">The decoded name/value field lines, in wire order.</param>
    /// <param name="trailers">The request's trailer collection.</param>
    /// <exception cref="InvalidDataException">Thrown when the trailer section violates an HTTP/3 message rule.</exception>
    public static void AddTrailers(List<(string Name, string Value)> fields, HttpTrailerCollection trailers)
    {
        HttpTrailerFieldRules.AddReceivedFields(fields, trailers, "HTTP/3");
    }

    /// <summary>
    /// Encodes the field section of a bodyless final response the transport answers on its own —
    /// the <c>:status</c> pseudo-header and <c>content-length: 0</c> — used when a request is
    /// refused before it ever became an exchange: a request head over
    /// <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> (431), or a request-body limit violated while a request
    /// interceptor read the body (413, or 431 for its trailer section).
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
    /// pseudo-header set to the interim code followed by the supplied fields, less any
    /// connection-specific field (<see cref="HttpResponseFieldRules"/>), with <b>no</b>
    /// <c>Content-Length</c> (an interim response carries no body — RFC 9110 §15.2). Written as an
    /// additional HEADERS frame on the request stream, before the final HEADERS frame (RFC 9114 §4.1).
    /// </summary>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
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
                HttpResponseFieldRules.EnsureValidName(header.Key);

                // RFC 9114 §4.2 — an interim response is a field section like any other.
                bool sendable = HttpResponseFieldRules.IsSendable(header.Key, header.Value);

                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        HttpResponseFieldRules.EnsureValidValue(header.Key, value);

                        if (sendable)
                        {
                            fields.Add((header.Key.Value.ToLowerInvariant(), value));
                        }
                    }
                }
            }
        }

        return QPackFieldSectionEncoder.Encode(fields);
    }

    /// <summary>
    /// Encodes a response's trailer section (RFC 9114 §4.1): the staged fields with no pseudo-header
    /// field (RFC 9114 §4.3), written as a HEADERS frame after the last DATA frame.
    /// </summary>
    /// <param name="trailers">
    /// The staged trailer fields, already checked when they were added. Each is checked again against the
    /// field syntax here, because a value built over an array can change after it was staged.
    /// </param>
    /// <returns>The QPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
    public static byte[] EncodeTrailers(IHttpHeaderCollection trailers)
    {
        List<(string Name, string Value)> fields = new();

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> field in trailers)
        {
            string fieldValue = field.Value.Value;
            HttpResponseFieldRules.EnsureValidName(field.Key);
            HttpResponseFieldRules.EnsureValidValue(field.Key, fieldValue);
            fields.Add((field.Key.Value, fieldValue));
        }

        return QPackFieldSectionEncoder.Encode(fields);
    }

    /// <summary>
    /// Encodes the response field section for a <em>buffered</em> response: the section
    /// <see cref="EncodeResponseHeaders(Http3Context)"/> encodes, with a <c>content-length</c> synthesized
    /// from <paramref name="bodyBytes"/> when the application set none. The synthesized field is added to
    /// the response headers.
    /// </summary>
    /// <param name="context">The exchange whose response head is encoded.</param>
    /// <param name="bodyBytes">The buffered body.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">
    /// A field name is not a token, or a value holds a control character other than HTAB. The synthesized
    /// <c>content-length</c> is removed again first, so a response sent in this one's place is framed by its
    /// own body.
    /// </exception>
    public static byte[] EncodeResponseHeaders(Http3Context context, byte[] bodyBytes)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        if (headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            return EncodeResponseHeaders(context);
        }

        headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            return EncodeResponseHeaders(context);
        }
        catch (HttpInvalidResponseFieldException)
        {
            // A refused head leaves the response replaceable (#1183); a stale length would make the
            // replacement malformed (RFC 9114 §4.1.2) whenever its body differs.
            headers.Remove(HttpHeaderKey.ContentLength);
            throw;
        }
    }

    /// <summary>
    /// Encodes the response field section for an <em>incrementally streamed</em>
    /// response: the <c>:status</c> pseudo-header followed by the response headers,
    /// with <b>no</b> synthesized <c>Content-Length</c>. HTTP/3 delimits
    /// the body with the stream end, so a length is neither known up front nor
    /// required. A connection-specific field is skipped
    /// (<see cref="HttpResponseFieldRules"/>).
    /// </summary>
    /// <param name="context">The exchange whose response head is encoded.</param>
    /// <returns>The QPACK-encoded field section.</returns>
    /// <remarks>
    /// Every field is checked against the field syntax before the section is returned
    /// (<see cref="HttpResponseFieldRules.EnsureValidName"/>, <see cref="HttpResponseFieldRules.EnsureValidValue"/>),
    /// the connection-specific ones included, so a field that would split an HTTP/1.1 head is refused on
    /// every version alike. The encoder is static-only, so a refusal leaves no QPACK state behind.
    /// </remarks>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
    public static byte[] EncodeResponseHeaders(Http3Context context)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        List<(string Name, string Value)> fields =
        [
            (":status", ((int)context.Response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            HttpResponseFieldRules.EnsureValidName(header.Key);

            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-folded value is
            // forbidden.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        HttpResponseFieldRules.EnsureValidValue(header.Key, value);
                        fields.Add(("set-cookie", value));
                    }
                }

                continue;
            }

            string fieldValue = header.Value.Value;
            HttpResponseFieldRules.EnsureValidValue(header.Key, fieldValue);

            // RFC 9114 §4.2 — a connection-specific field would make the response malformed.
            if (HttpResponseFieldRules.IsSendable(header.Key, header.Value))
            {
                fields.Add((header.Key.Value, fieldValue));
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
            path = DecodePath(requestTarget[..queryIndex]);
            return new HttpQuery(requestTarget[(queryIndex + 1)..]).Parse();
        }

        path = DecodePath(requestTarget);
        return new HttpQueryCollection();
    }

    /// <summary>
    /// Percent-decodes the path component of the <c>:path</c> pseudo-header through the
    /// <see cref="HttpPath.FromUriComponent"/> decode HTTP/1.1 and HTTP/2 share (RFC 3986 §2.4).
    /// </summary>
    /// <param name="pathComponent">The <c>:path</c> value up to its query.</param>
    /// <returns>The decoded path.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the value does not decode to a legal path — a decoded space, control character,
    /// <c>?</c>, <c>#</c>, or NUL, an illegal character sent literally, or no leading <c>/</c>. That
    /// makes the request malformed (RFC 9114 §4.1.2), reported like every other rule in this codec,
    /// so the connection context resets the request stream alone with <c>H3_MESSAGE_ERROR</c>. The
    /// decode itself is unchanged (h1/h2/h3 parity); only the failure's scope is.
    /// </exception>
    private static HttpPath DecodePath(string pathComponent)
    {
        try
        {
            return HttpPath.FromUriComponent(pathComponent);
        }
        catch (Exception exception) when (exception is HttpException or InvalidOperationException)
        {
            // HttpPath rejects an illegal character or a missing leading '/' with an HttpException;
            // the URL decoder rejects a decoded NUL with an InvalidOperationException.
            throw new InvalidDataException(
                $"HTTP/3 request carries a malformed :path pseudo-header (RFC 9114 §4.1.2): {exception.Message}",
                exception);
        }
    }
}
