using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a back-channel logout request (Back-Channel Logout 1.0 §2.5): the delivery
/// of a logout token to a relying party's back-channel logout endpoint. The shared logout
/// members (issuer, subject, provider session identifiers) drive protocol-neutral
/// single-logout orchestration; populate them from the parsed token with
/// <see cref="OpenIdConnectBackChannelLogoutRequestDescriptor.Apply" />.
/// </summary>
public sealed class OpenIdConnectBackChannelLogoutRequest : ProtocolLogoutRequest
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="OpenIdConnectBackChannelLogoutRequest" /> class by snapshotting the
    /// provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a session identifier entry is null or whitespace, or when a property
    /// name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when a parsed token is present but the descriptor's shared logout members
    /// disagree with the token's claims — internal consistency between the two
    /// representations is structural.
    /// </exception>
    public OpenIdConnectBackChannelLogoutRequest(OpenIdConnectBackChannelLogoutRequestDescriptor descriptor)
        : base(GuardAgreement(descriptor), AuthenticationProtocol.OpenIdConnect)
    {
        LogoutToken = descriptor.LogoutToken;
        Token = descriptor.Token;
    }

    /// <summary>
    /// Gets the logout token, as the raw compact string.
    /// </summary>
    public string? LogoutToken { get; }

    /// <summary>
    /// Gets the parsed logout token, when available.
    /// </summary>
    public OpenIdConnectLogoutToken? Token { get; }

    private static OpenIdConnectBackChannelLogoutRequestDescriptor GuardAgreement(
        OpenIdConnectBackChannelLogoutRequestDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var token = descriptor.Token;
        if (token is null)
        {
            return descriptor;
        }

        if (token.Issuer is not null &&
            !string.Equals(descriptor.Issuer, token.Issuer, StringComparison.Ordinal))
        {
            throw new IdentityModelException(
                "The descriptor's issuer disagrees with the parsed logout token's iss claim. " +
                "Populate the shared members with Apply(token).");
        }

        if (token.Subject is not null &&
            !string.Equals(descriptor.Subject?.Value, token.Subject, StringComparison.Ordinal))
        {
            throw new IdentityModelException(
                "The descriptor's subject disagrees with the parsed logout token's sub claim. " +
                "Populate the shared members with Apply(token).");
        }

        if (token.SessionId is not null)
        {
            var containsSession = false;
            for (var index = 0; index < descriptor.ProviderSessionIds.Count; index++)
            {
                containsSession |= string.Equals(
                    descriptor.ProviderSessionIds[index], token.SessionId, StringComparison.Ordinal);
            }

            if (!containsSession)
            {
                throw new IdentityModelException(
                    "The descriptor's provider session identifiers disagree with the parsed logout token's " +
                    "sid claim. Populate the shared members with Apply(token).");
            }
        }

        return descriptor;
    }
}
