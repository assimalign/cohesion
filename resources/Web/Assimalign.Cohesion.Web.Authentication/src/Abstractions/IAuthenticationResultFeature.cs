using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The per-request feature that carries the result of authenticating the request against the
/// default authenticate scheme, sitting alongside <see cref="IAuthenticationFeature"/> in the
/// context's feature collection.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="IAuthenticationFeature"/> holds only the resolved
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> (<c>context.User</c>), this feature holds
/// the richer <see cref="AuthenticateResult"/> — including its ticket, properties, and any failure
/// — so authorization, diagnostics, and result writers can inspect <em>how</em> the principal was
/// established without re-running the scheme.
/// </para>
/// <para>
/// The authentication service installs it when it authenticates the default scheme (typically
/// from the authentication middleware). It mirrors the ASP.NET Core
/// <c>IAuthenticateResultFeature</c>.
/// </para>
/// </remarks>
public interface IAuthenticationResultFeature : IHttpFeature
{
    /// <summary>
    /// Gets or sets the result of authenticating the request against the default authenticate
    /// scheme.
    /// </summary>
    AuthenticateResult? AuthenticateResult { get; set; }
}
