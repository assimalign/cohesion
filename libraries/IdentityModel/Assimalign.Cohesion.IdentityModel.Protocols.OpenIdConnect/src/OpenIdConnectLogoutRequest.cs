using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an RP-initiated logout request (RP-Initiated Logout 1.0 §2). The
/// <c>state</c> parameter rides the inherited
/// <see cref="ProtocolMessage.CorrelationState" />.
/// </summary>
public sealed class OpenIdConnectLogoutRequest : ProtocolLogoutRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectLogoutRequest" /> class
    /// by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a list entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    public OpenIdConnectLogoutRequest(OpenIdConnectLogoutRequestDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
        IdTokenHint = descriptor.IdTokenHint;
        LogoutHint = descriptor.LogoutHint;
        PostLogoutRedirectUri = descriptor.PostLogoutRedirectUri;
        UiLocales = ModelSnapshot.Strings(descriptor.UiLocales, nameof(descriptor));
    }

    /// <summary>
    /// Gets the client identifier, when sent. Alias of the base envelope's
    /// <see cref="ProtocolMessage.Issuer" />.
    /// </summary>
    public string? ClientId => Issuer;

    /// <summary>
    /// Gets the ID token hint, as the original compact token issued at login.
    /// </summary>
    public string? IdTokenHint { get; }

    /// <summary>
    /// Gets the logout hint — who to log out.
    /// </summary>
    public string? LogoutHint { get; }

    /// <summary>
    /// Gets the post-logout redirect URI, as the exact wire string compared ordinally.
    /// </summary>
    public string? PostLogoutRedirectUri { get; }

    /// <summary>
    /// Gets the preferred UI locales.
    /// </summary>
    public IReadOnlyList<string> UiLocales { get; }
}
