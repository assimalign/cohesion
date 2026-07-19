using System.Security.Claims;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The authentication feature for an HTTP exchange.
/// </summary>
/// <remarks>
/// <para>
/// Authentication middleware constructs an implementation, sets
/// <see cref="User"/> to the resolved <see cref="ClaimsPrincipal"/>,
/// and attaches it via
/// <c>context.Features.Set&lt;IHttpAuthenticationFeature&gt;(feature)</c>.
/// Downstream code reads
/// <see cref="HttpContextAuthenticationExtensions.User"/> on the
/// context to access the principal without binding to this interface
/// directly.
/// </para>
/// <para>
/// Future iterations of this package may extend the contract with
/// auth-flow methods (<c>AuthenticateAsync</c>, <c>SignInAsync</c>,
/// <c>SignOutAsync</c>, <c>ChallengeAsync</c>, <c>ForbidAsync</c>) when
/// authentication middleware lands; for now the surface is intentionally
/// the smallest thing that lets the protocol core drop its
/// <c>ClaimsPrincipal</c> dependency.
/// </para>
/// </remarks>
public interface IAuthenticationFeature : IHttpFeature
{
    /// <summary>
    /// Gets or sets the principal authenticated for this exchange.
    /// </summary>
    ClaimsPrincipal User { get; set; }
}
