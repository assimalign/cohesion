using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Accumulates a decoded HTTP/2 field section while enforcing RFC 9113
/// §8.2 (field validity) and §8.3 (pseudo-header order) rules. Pseudo-headers
/// (<c>:method</c>, <c>:path</c>, <c>:scheme</c>, <c>:authority</c>,
/// <c>:protocol</c>) are surfaced as typed properties; ordinary fields
/// land in <see cref="Headers"/>.
/// </summary>
/// <remarks>
/// <para>
/// The section is folded only after the whole block is decoded (<see cref="HPackDecoder.DecodeRequestHeaders"/>),
/// so a field rule broken here is reported as an <see cref="InvalidDataException"/>, never as an HPACK
/// decoding failure.
/// </para>
/// <para>
/// Whether the pseudo-header fields make a complete request — none missing, none repeated, no empty
/// <c>:path</c> (RFC 9113 §8.3.1) — is judged by the stream once the section is folded, because a
/// malformed request costs only its stream (RFC 9113 §8.1.1). A repeated pseudo-header is therefore
/// recorded in <see cref="RepeatedPseudoHeader"/> rather than thrown, and an empty <c>:path</c> is kept
/// as it arrived.
/// </para>
/// </remarks>
internal sealed class HPackDecodedHeaders
{
    private bool _sawRegularField;
    // The crumbs of a split Cookie field, joined once the section is complete.
    private HttpCookieCrumbs _cookieCrumbs;

    public HPackDecodedHeaders()
    {
        Headers = new HttpHeaderCollection();
    }

    public string? Authority { get; private set; }

    public string? Method { get; private set; }

    public string? Path { get; private set; }

    public string? Scheme { get; private set; }

    /// <summary>
    /// The <c>:protocol</c> pseudo-header (RFC 8441), present only on an
    /// extended CONNECT request. <see langword="null"/> for ordinary requests.
    /// </summary>
    public string? Protocol { get; private set; }

    /// <summary>
    /// The name of the first pseudo-header field the section repeats, or <see langword="null"/> when
    /// none is repeated. The first value is the one kept. A repeated pseudo-header makes the request
    /// malformed (RFC 9113 §8.3).
    /// </summary>
    public string? RepeatedPseudoHeader { get; private set; }

    public HttpHeaderCollection Headers { get; }

    /// <summary>
    /// Folds a decoded (name, value) pair into the accumulating field
    /// section, applying the RFC 9113 §8 validation rules.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The field breaks a field rule: an empty name, a name that is not a lowercase token, a value with
    /// NUL, CR, LF, another control character but HTAB, or whitespace at either end (#1376,
    /// <see cref="HttpReceivedFieldRules"/>), a connection-specific field, a <c>TE</c> other than
    /// <c>trailers</c>, a pseudo-header after a regular field, or a pseudo-header not defined for
    /// requests. The field was decoded, so this is not an HPACK failure: the request is malformed, and
    /// the stream resets itself with <c>PROTOCOL_ERROR</c> (RFC 9113 §8.1.1).
    /// </exception>
    public void Add(string name, string value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException("The HTTP/2 field name cannot be empty.");
        }

        // RFC 9113 §8.2.1 — a value, a pseudo-header's included, holds no NUL, CR, or LF and no
        // whitespace at either end; the core rule also refuses the other control characters (#1376).
        HttpReceivedFieldRules.EnsureValidValue(name, value, "HTTP/2");

        if (name[0] == ':')
        {
            AddPseudoHeader(name, value);
            return;
        }

        // RFC 9113 §8.2.1 — a regular field name is a lowercase token, so it holds no ':', SP, or
        // control character. RFC 9113 §8.3 — pseudo-header fields MUST appear before any regular
        // field. Once a regular field has been observed, any subsequent pseudo-header is malformed.
        HttpReceivedFieldRules.EnsureValidName(name, "HTTP/2");
        _sawRegularField = true;

        // RFC 9113 §8.2.2 — connection-specific header fields are
        // forbidden in HTTP/2. They MUST be treated as malformed.
        RejectIfConnectionSpecific(name, value);

        HttpHeaderKey key = new(name);

        if (Headers.TryGetValue(key, out HttpHeaderValue existingValue))
        {
            // RFC 9113 §8.2.3 — repeated-field combining (Cookie coalesces with
            // "; "; other list fields combine as distinct values) is the same
            // rule for HTTP/2 and HTTP/3: cookie crumbs collect in HttpCookieCrumbs
            // and join once in Complete, and other fields combine through
            // HttpFieldNormalization. Both run in time linear in the repeats.
            if (!_cookieCrumbs.TryAdd(key, existingValue, value))
            {
                Headers[key] = HttpFieldNormalization.CombineFieldValue(key, existingValue, value);
            }
        }
        else
        {
            Headers[key] = value;
        }
    }

    /// <summary>
    /// Ends the field section: joins the crumbs of a split <c>Cookie</c> field into the one value
    /// <see cref="Headers"/> carries (RFC 9113 §8.2.3). Called once, after the last <see cref="Add"/>.
    /// </summary>
    public void Complete()
    {
        _cookieCrumbs.Join(Headers);
    }

    private void AddPseudoHeader(string name, string value)
    {
        // RFC 9113 §8.3 — pseudo-header fields MUST appear before any
        // regular field. If a regular field has already been seen, the
        // section is malformed.
        if (_sawRegularField)
        {
            throw new InvalidDataException(
                $"Pseudo-header field {HttpReceivedFieldRules.DescribeName(name)} appeared after regular fields; pseudo-headers MUST come first.");
        }

        // RFC 9113 §8.3 — each pseudo-header field appears at most once. A repeat is recorded for the
        // stream to judge (see the remarks on this type); an empty :path is likewise kept for it.
        switch (name)
        {
            case ":authority":
                Authority = AssignOnce(Authority, value, name);
                return;

            case ":method":
                Method = AssignOnce(Method, value, name);
                return;

            case ":path":
                Path = AssignOnce(Path, value, name);
                return;

            case ":scheme":
                Scheme = AssignOnce(Scheme, value, name);
                return;

            case ":protocol":
                // RFC 8441 §4 — the extended CONNECT protocol indicator. Its
                // CONNECT-only / required-companion rules are cross-field and
                // are validated once the whole section is decoded
                // (HttpFieldNormalization.ValidateExtendedConnect).
                Protocol = AssignOnce(Protocol, value, name);
                return;

            case ":status":
                // RFC 9113 §8.3 — :status is a response pseudo-header.
                // Receiving it in a request field section is malformed.
                throw new InvalidDataException(
                    "Pseudo-header field ':status' is response-only and MUST NOT appear in a request.");

            default:
                // RFC 9113 §8.3 — pseudo-header names that are not
                // defined for the given message type are malformed.
                throw new InvalidDataException(
                    $"Unknown pseudo-header field {HttpReceivedFieldRules.DescribeName(name)}.");
        }
    }

    /// <summary>
    /// Returns the value a pseudo-header keeps: <paramref name="value"/> the first time
    /// <paramref name="name"/> appears, and <paramref name="current"/> after that, recording the repeat
    /// in <see cref="RepeatedPseudoHeader"/>.
    /// </summary>
    private string AssignOnce(string? current, string value, string name)
    {
        if (current is null)
        {
            return value;
        }

        RepeatedPseudoHeader ??= name;
        return current;
    }

    private static void RejectIfConnectionSpecific(string name, string value)
    {
        HttpHeaderKey key = new(name);

        // RFC 9113 §8.2.2 — connection-specific header fields are forbidden in
        // HTTP/2 (the rule is shared with HTTP/3 and lives in
        // HttpFieldNormalization so both versions reject the same set).
        if (HttpFieldNormalization.IsForbiddenInHttp2Or3(key))
        {
            throw new InvalidDataException(
                $"Connection-specific header field '{name}' is forbidden in HTTP/2 (RFC 9113 §8.2.2).");
        }

        // RFC 9113 §8.2.2 — TE is the one exception: it MAY appear, but its
        // value MUST be exactly "trailers".
        if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase)
            && !HttpFieldNormalization.IsTeValueValidInHttp2Or3(value))
        {
            throw new InvalidDataException(
                $"HTTP/2 field 'TE' MUST carry only the value 'trailers'; got '{value}'.");
        }
    }
}
