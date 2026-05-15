using System;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

/// <summary>
/// Accumulates a decoded HTTP/2 field section while enforcing RFC 9113
/// §8.2 (field validity) and §8.3 (pseudo-header order + completeness)
/// rules. Pseudo-headers (<c>:method</c>, <c>:path</c>, <c>:scheme</c>,
/// <c>:authority</c>) are surfaced as typed properties; ordinary fields
/// land in <see cref="Headers"/>.
/// </summary>
internal sealed class HPackDecodedHeaders
{
    private bool _sawRegularField;

    public HPackDecodedHeaders()
    {
        Headers = new HttpHeaderCollection();
    }

    public string? Authority { get; private set; }

    public string? Method { get; private set; }

    public string? Path { get; private set; }

    public string? Scheme { get; private set; }

    public HttpHeaderCollection Headers { get; }

    /// <summary>
    /// Folds a decoded (name, value) pair into the accumulating field
    /// section, applying the RFC 9113 §8 validation rules. Failures
    /// surface as <see cref="HPackDecodingException"/>; the caller maps
    /// these to a connection-level PROTOCOL_ERROR via
    /// <c>Http2ConnectionException</c>.
    /// </summary>
    public void Add(string name, string value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new HPackDecodingException("The HTTP/2 field name cannot be empty.");
        }

        if (name[0] == ':')
        {
            AddPseudoHeader(name, value);
            return;
        }

        // RFC 9113 §8.3 — pseudo-header fields MUST appear before any
        // regular field. Once a regular field has been observed, any
        // subsequent pseudo-header is malformed.
        ValidateRegularFieldName(name);
        _sawRegularField = true;

        // RFC 9113 §8.2.2 — connection-specific header fields are
        // forbidden in HTTP/2. They MUST be treated as malformed.
        RejectIfConnectionSpecific(name, value);

        HttpHeaderKey key = new(name);

        if (Headers.TryGetValue(key, out HttpHeaderValue existingValue))
        {
            // RFC 9113 §8.2.3 — multiple Cookie fields in an HTTP/2
            // field section MUST be coalesced into a single field with
            // "; " separator before forwarding to HTTP/1.1 semantics.
            Headers[key] = string.Equals(name, "cookie", StringComparison.OrdinalIgnoreCase)
                ? string.Concat(existingValue.Value, "; ", value)
                : HttpHeaderValue.Concat(existingValue, value);
        }
        else
        {
            Headers[key] = value;
        }
    }

    private void AddPseudoHeader(string name, string value)
    {
        // RFC 9113 §8.3 — pseudo-header fields MUST appear before any
        // regular field. If a regular field has already been seen, the
        // section is malformed.
        if (_sawRegularField)
        {
            throw new HPackDecodingException(
                $"Pseudo-header field '{name}' appeared after regular fields; pseudo-headers MUST come first.");
        }

        switch (name)
        {
            case ":authority":
                if (Authority is not null)
                {
                    throw new HPackDecodingException(
                        "Pseudo-header field ':authority' MUST NOT appear more than once.");
                }

                Authority = value;
                return;

            case ":method":
                if (Method is not null)
                {
                    throw new HPackDecodingException(
                        "Pseudo-header field ':method' MUST NOT appear more than once.");
                }

                Method = value;
                return;

            case ":path":
                if (Path is not null)
                {
                    throw new HPackDecodingException(
                        "Pseudo-header field ':path' MUST NOT appear more than once.");
                }

                if (string.IsNullOrEmpty(value))
                {
                    throw new HPackDecodingException(
                        "Pseudo-header field ':path' MUST NOT be empty (except for OPTIONS *, encoded as '*').");
                }

                Path = value;
                return;

            case ":scheme":
                if (Scheme is not null)
                {
                    throw new HPackDecodingException(
                        "Pseudo-header field ':scheme' MUST NOT appear more than once.");
                }

                Scheme = value;
                return;

            case ":status":
                // RFC 9113 §8.3 — :status is a response pseudo-header.
                // Receiving it in a request field section is malformed.
                throw new HPackDecodingException(
                    "Pseudo-header field ':status' is response-only and MUST NOT appear in a request.");

            default:
                // RFC 9113 §8.3 — pseudo-header names that are not
                // defined for the given message type are malformed.
                throw new HPackDecodingException(
                    $"Unknown pseudo-header field '{name}'.");
        }
    }

    private static void ValidateRegularFieldName(string name)
    {
        // RFC 9113 §8.2.1 — field names MUST be lowercase. Uppercase
        // letters in a field name are malformed.
        foreach (char c in name)
        {
            if (c >= 'A' && c <= 'Z')
            {
                throw new HPackDecodingException(
                    $"Field name '{name}' contains uppercase characters; HTTP/2 field names MUST be lowercase.");
            }
        }
    }

    private static void RejectIfConnectionSpecific(string name, string value)
    {
        // RFC 9113 §8.2.2 — these connection-specific header fields are
        // forbidden in HTTP/2 because their semantics conflict with the
        // protocol's multiplexed framing.
        if (string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "proxy-connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "keep-alive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "transfer-encoding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "upgrade", StringComparison.OrdinalIgnoreCase))
        {
            throw new HPackDecodingException(
                $"Connection-specific header field '{name}' is forbidden in HTTP/2 (RFC 9113 §8.2.2).");
        }

        // RFC 9113 §8.2.2 — TE is the one exception: it MAY appear, but
        // its value MUST be exactly "trailers".
        if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "trailers", StringComparison.OrdinalIgnoreCase))
        {
            throw new HPackDecodingException(
                $"HTTP/2 field 'TE' MUST carry only the value 'trailers'; got '{value}'.");
        }
    }
}
