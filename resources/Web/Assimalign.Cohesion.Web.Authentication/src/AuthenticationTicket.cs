using System;
using System.Security.Claims;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The immutable success payload of an authentication: the authenticated
/// <see cref="ClaimsPrincipal"/>, the <see cref="AuthenticationProperties"/> that travelled
/// with it, and the name of the scheme that produced it.
/// </summary>
/// <remarks>
/// The ticket is the Web layer's <see cref="ClaimsPrincipal"/>-based analogue of the
/// IdentityModel <c>AuthenticationResult</c>: both model a successful authentication as a value,
/// but the Web pipeline works in <see cref="ClaimsPrincipal"/> (matching <c>context.User</c>),
/// whereas IdentityModel works in its own subject model. Bridging between the two is the bearer
/// handler's job when it materializes a validated token onto a principal.
/// </remarks>
public sealed class AuthenticationTicket
{
    /// <summary>
    /// Initializes a new ticket.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <param name="properties">The properties that travelled with the principal, or <see langword="null"/> for empty properties.</param>
    /// <param name="authenticationScheme">The name of the scheme that produced the ticket.</param>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authenticationScheme"/> is <see langword="null"/> or whitespace.</exception>
    public AuthenticationTicket(ClaimsPrincipal principal, AuthenticationProperties? properties, string authenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        Principal = principal;
        Properties = properties ?? new AuthenticationProperties();
        AuthenticationScheme = authenticationScheme;
    }

    /// <summary>
    /// Gets the name of the scheme that produced the ticket.
    /// </summary>
    public string AuthenticationScheme { get; }

    /// <summary>
    /// Gets the authenticated principal.
    /// </summary>
    public ClaimsPrincipal Principal { get; }

    /// <summary>
    /// Gets the properties that travelled with the principal. Never <see langword="null"/>.
    /// </summary>
    public AuthenticationProperties Properties { get; }
}
