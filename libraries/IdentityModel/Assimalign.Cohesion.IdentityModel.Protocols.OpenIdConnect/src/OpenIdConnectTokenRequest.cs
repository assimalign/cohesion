using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a token endpoint request.
/// </summary>
public sealed class OpenIdConnectTokenRequest : ProtocolRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectTokenRequest" /> class
    /// by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a scope entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no grant type — the member that makes the artifact
    /// recognizable as a token request.
    /// </exception>
    public OpenIdConnectTokenRequest(OpenIdConnectTokenRequestDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
        if (string.IsNullOrWhiteSpace(descriptor.GrantType))
        {
            throw new IdentityModelException("A token request requires a grant type.");
        }

        GrantType = descriptor.GrantType;
        Code = descriptor.Code;
        RedirectUri = descriptor.RedirectUri;
        CodeVerifier = descriptor.CodeVerifier;
        RefreshToken = descriptor.RefreshToken;
        Scopes = ModelSnapshot.Strings(descriptor.Scopes, nameof(descriptor));
        ClientAssertion = descriptor.ClientAssertion;
        ClientAssertionType = descriptor.ClientAssertionType;
    }

    /// <summary>
    /// Gets the client identifier, when carried in the request body. Alias of the base
    /// envelope's <see cref="ProtocolMessage.Issuer" />.
    /// </summary>
    public string? ClientId => Issuer;

    /// <summary>
    /// Gets the grant type.
    /// </summary>
    public string GrantType { get; }

    /// <summary>
    /// Gets the authorization code, for the authorization code grant.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Gets the redirect URI the code was issued to.
    /// </summary>
    public string? RedirectUri { get; }

    /// <summary>
    /// Gets the PKCE code verifier.
    /// </summary>
    public string? CodeVerifier { get; }

    /// <summary>
    /// Gets the refresh token, for the refresh grant.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>
    /// Gets the requested scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Gets the client assertion, as the raw compact JWT.
    /// </summary>
    public string? ClientAssertion { get; }

    /// <summary>
    /// Gets the client assertion type.
    /// </summary>
    public string? ClientAssertionType { get; }
}
