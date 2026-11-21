using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The OAuth grants an app registration may use to obtain tokens. The member values are stable
/// and additive-only.
/// </summary>
/// <remarks>
/// A registration lists its grants explicitly, as the <c>grant_types</c> of RFC 7591 do, and a
/// grant it does not list is refused whatever else the registration holds. The implicit grant
/// and the resource owner password credentials grant are deliberately absent: OAuth 2.1 removes
/// both. A registration with no grant obtains no token and only exposes an API.
/// </remarks>
[Flags]
public enum IdentityClientGrants
{
    /// <summary>
    /// The registration obtains no token.
    /// </summary>
    None = 0,

    /// <summary>
    /// The authorization code grant of RFC 6749 section 4.1, always with the proof key of
    /// RFC 7636. It needs at least one redirect URI.
    /// </summary>
    AuthorizationCode = 1,

    /// <summary>
    /// The client credentials grant of RFC 6749 section 4.4, in which the application acts as
    /// itself. It needs at least one credential.
    /// </summary>
    ClientCredentials = 2,

    /// <summary>
    /// The device authorization grant of RFC 8628, for a device that cannot show a browser.
    /// </summary>
    DeviceCode = 4,

    /// <summary>
    /// The refresh token grant of RFC 6749 section 6. It extends a grant made by a user, so it
    /// needs <see cref="AuthorizationCode"/> or <see cref="DeviceCode"/>.
    /// </summary>
    RefreshToken = 8,
}
