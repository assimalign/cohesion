using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML <c>Response</c> before it is materialized into an
/// immutable <see cref="SamlResponse" />.
/// </summary>
/// <remarks>
/// The base <see cref="ProtocolResponseDescriptor.Status" /> carries the SAML status: the
/// top-level <c>StatusCode</c> value is the status code, nested status codes are its
/// sub-codes, and the response succeeds only when the top-level code is
/// <see cref="SamlStatusCodes.Success" />. A single-logout partial result is a success with a
/// <see cref="SamlStatusCodes.PartialLogout" /> sub-code, built via
/// <see cref="ProtocolResponseStatus.SuccessWith" />.
/// </remarks>
public class SamlResponseDescriptor : ProtocolResponseDescriptor
{
    /// <summary>
    /// Gets the assertions carried by the response.
    /// </summary>
    public IList<SamlAssertion> Assertions { get; } = new List<SamlAssertion>();

    /// <summary>
    /// Gets the encrypted assertion markers the descriptive layer cannot open. The token
    /// package decrypts each and re-materializes it as a <see cref="SamlAssertion" />.
    /// </summary>
    public IList<SamlEncryptedElement> EncryptedAssertions { get; } = new List<SamlEncryptedElement>();

    /// <summary>
    /// Gets or sets the verbatim, as-received <c>&lt;samlp:Response&gt;</c> element octets,
    /// preserved for response-level signature re-verification. Must be the exact response
    /// element, never a re-serialization.
    /// </summary>
    public string? RawXml { get; set; }
}
