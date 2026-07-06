using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML 2.0 binding URIs and their mapping onto the family's transport-shaped
/// <see cref="ProtocolBinding" /> vocabulary. The wire URIs live here (the owning branch);
/// <see cref="ToProtocolBinding" /> is the single place SAML bindings map to the neutral
/// vocabulary, mirroring how the OpenID Connect branch maps response modes.
/// </summary>
public static class SamlBindings
{
    /// <summary>The HTTP Redirect binding URI.</summary>
    public const string HttpRedirect = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";

    /// <summary>The HTTP POST binding URI.</summary>
    public const string HttpPost = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    /// <summary>The HTTP Artifact binding URI.</summary>
    public const string HttpArtifact = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Artifact";

    /// <summary>The SOAP binding URI.</summary>
    public const string Soap = "urn:oasis:names:tc:SAML:2.0:bindings:SOAP";

    /// <summary>The reverse SOAP (PAOS) binding URI.</summary>
    public const string Paos = "urn:oasis:names:tc:SAML:2.0:bindings:PAOS";

    /// <summary>
    /// Maps a SAML binding URI onto the family's transport-shaped binding vocabulary.
    /// </summary>
    /// <param name="samlBindingUri">The SAML binding URI.</param>
    /// <returns>
    /// The matching binding, or <see cref="ProtocolBinding.Unknown" /> when the URI is not
    /// recognized.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="samlBindingUri" /> is null or whitespace.</exception>
    public static ProtocolBinding ToProtocolBinding(string samlBindingUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(samlBindingUri);

        return samlBindingUri switch
        {
            HttpRedirect => ProtocolBinding.HttpRedirect,
            HttpPost => ProtocolBinding.HttpPost,
            HttpArtifact => ProtocolBinding.HttpArtifact,
            Soap or Paos => ProtocolBinding.Soap,
            _ => ProtocolBinding.Unknown,
        };
    }
}
