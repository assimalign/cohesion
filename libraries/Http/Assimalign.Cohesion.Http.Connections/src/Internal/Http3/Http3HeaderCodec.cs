using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// Bridges QPACK field sections (RFC 9204) to the HTTP message model and
/// enforces the HTTP/3 field-section rules (RFC 9114 §4.2 / §4.3): the
/// pseudo-header set, pseudo-before-regular ordering, lowercase field
/// names, connection-specific field prohibition, and required request
/// pseudo-headers. The QPACK dynamic table is disabled, so encoding and
/// decoding reference only the static table or literals.
/// </summary>
internal static class Http3HeaderCodec
{
    public static Http3Request DecodeRequestHeaders(ReadOnlySpan<byte> headerBlock, HttpScheme fallbackScheme, byte[] bodyBytes, out string? extendedConnectProtocol)
    {
        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(headerBlock);

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

        return new Http3Request(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(method),
            scheme,
            query,
            headers,
            new MemoryStream(bodyBytes, writable: false));
    }

    public static byte[] EncodeResponseHeaders(Http3Context context, byte[] bodyBytes)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

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
