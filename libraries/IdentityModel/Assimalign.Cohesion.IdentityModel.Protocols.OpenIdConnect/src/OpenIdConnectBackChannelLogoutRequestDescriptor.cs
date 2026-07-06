using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of a back-channel logout request before it is materialized into
/// an immutable <see cref="OpenIdConnectBackChannelLogoutRequest" />.
/// </summary>
public class OpenIdConnectBackChannelLogoutRequestDescriptor : ProtocolLogoutRequestDescriptor
{
    /// <summary>
    /// Gets or sets the logout token (<c>logout_token</c>), as the raw compact string.
    /// </summary>
    public string? LogoutToken { get; set; }

    /// <summary>
    /// Gets or sets the parsed logout token, when available. Use <see cref="Apply" /> to
    /// populate the shared logout members from it — the base envelope snapshots the
    /// descriptor, so the token's subject and session data must be on the descriptor
    /// before materialization.
    /// </summary>
    public OpenIdConnectLogoutToken? Token { get; set; }

    /// <summary>
    /// Populates the shared logout members from a parsed logout token: the base issuer,
    /// the canonical subject (derived through the pinned wire-only recipe), the provider
    /// session identifiers, the issuance instant, and the raw token string.
    /// </summary>
    /// <param name="token">The parsed logout token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="token" /> is null.</exception>
    public void Apply(OpenIdConnectLogoutToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        Token = token;
        LogoutToken = token.RawToken ?? LogoutToken;
        Issuer = token.Issuer;
        IssuedAt = token.IssuedAt;
        Subject = token.GetSubjectIdentifier();

        ProviderSessionIds.Clear();
        if (token.SessionId is not null)
        {
            ProviderSessionIds.Add(token.SessionId);
        }
    }
}
