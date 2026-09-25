namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the contents of a protocol response before it is materialized into a
/// <see cref="ProtocolResponse" /> derivative.
/// </summary>
public abstract class ProtocolResponseDescriptor : ProtocolMessageDescriptor
{
    /// <summary>
    /// Gets or sets the identifier of the request message this response answers (a SAML
    /// <c>InResponseTo</c>). Message-identifier correlation only — always null for
    /// protocols without message identifiers (OpenID Connect); the opaque round-trip value
    /// belongs on <see cref="ProtocolMessageDescriptor.CorrelationState" />.
    /// </summary>
    public string? InResponseTo { get; set; }

    /// <summary>
    /// Gets or sets the response outcome. Required at materialization: a response with an
    /// unmapped status must fail construction rather than default to success, and
    /// protocols whose success responses carry no status element (OpenID Connect) set
    /// <see cref="ProtocolResponseStatus.Success" /> explicitly in their materializers.
    /// </summary>
    public ProtocolResponseStatus? Status { get; set; }
}
