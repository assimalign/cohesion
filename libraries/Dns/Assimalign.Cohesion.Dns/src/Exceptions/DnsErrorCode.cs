namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Strongly-typed error categories surfaced through <see cref="DnsException"/>. The enum keeps
/// callers from string-matching exception messages and lets every Cohesion DNS provider
/// (client, resolver, authority, transport) raise a uniform error surface.
/// </summary>
/// <remarks>
/// <para>
/// Numeric values are stable. Adding a new code is additive; renaming or renumbering an
/// existing code is a breaking change. <see cref="Other"/> is reserved for failures that do
/// not fit any other category and SHOULD always carry a wrapped inner exception so callers
/// can inspect the original failure.
/// </para>
/// <para>
/// The wire-level DNS RCODE values (NoError, FormErr, ServFail, NXDomain, etc.) live on
/// <see cref="DnsResponseCode"/>. This enum models <em>client-side</em> failure categories;
/// the two are correlated but distinct.
/// </para>
/// </remarks>
public enum DnsErrorCode
{
    /// <summary>
    /// Fallback for failures that don't fit any other category. Always pair with an inner
    /// exception that carries the underlying cause.
    /// </summary>
    Other = 0,

    /// <summary>
    /// The requested name has no records of the requested type (corresponds to the wire
    /// <see cref="DnsResponseCode.NXDomain"/> response code, or an authoritative empty answer).
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The remote name server replied with a non-success RCODE other than NXDomain (FormErr,
    /// ServFail, NotImplemented, Refused, etc.). Inspect <see cref="DnsException.ResponseCode"/>
    /// for the specific RCODE.
    /// </summary>
    ServerFailure = 2,

    /// <summary>
    /// A query was sent but no response was received within the configured timeout.
    /// </summary>
    Timeout = 3,

    /// <summary>
    /// The bytes received from the network could not be parsed as a valid DNS message
    /// (truncated header, malformed name compression, oversized RR, etc.).
    /// </summary>
    Malformed = 4,

    /// <summary>
    /// A response was received but its identity did not match the outgoing query (mismatched
    /// transaction id, question section, or signed-by-someone-else cookie). Treat as a
    /// potential spoofing attempt.
    /// </summary>
    Spoofed = 5,

    /// <summary>
    /// DNSSEC validation failed for the response (bad signature, expired RRSIG, broken chain
    /// of trust, denial-of-existence proof failure).
    /// </summary>
    DnssecValidationFailed = 6,

    /// <summary>
    /// The transport refused the connection or the underlying socket reported a non-recoverable
    /// I/O error.
    /// </summary>
    Transport = 7,

    /// <summary>
    /// The operation was rejected because the target resolver / authority / zone is read-only
    /// or otherwise rejects mutating requests.
    /// </summary>
    ReadOnly = 8,

    /// <summary>
    /// A TSIG-signed request or response failed signature verification or carried an unknown
    /// key identifier.
    /// </summary>
    TsigVerificationFailed = 9,
}
