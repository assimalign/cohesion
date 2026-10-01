using System;
using System.Security.Claims;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// The input of one authorization evaluation: the principal being authorized and the exchange it
/// made. Every <see cref="IAuthorizationRequirement"/> of a policy, including delegate assertions,
/// receives the same instance.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="User"/> is the principal the policy is evaluated against. When the policy names
/// authentication schemes, it is the principal those schemes produced for the request (their
/// identities combined, in scheme order); otherwise it is <c>context.User</c> as
/// <c>UseAuthentication</c> established it. Requirements read the principal from here rather than
/// from <see cref="HttpContext"/>, so the same requirement evaluates correctly whichever schemes the
/// policy names.
/// </para>
/// <para>
/// <see cref="HttpContext"/> carries the rest of the request: route values, headers, and the matched
/// endpoint's metadata (<c>context.HttpContext.GetEndpointMetadata&lt;T&gt;()</c>), for requirements that
/// depend on more than the principal. A requirement must not write the response; failures are
/// answered by the policy's authentication schemes.
/// </para>
/// </remarks>
public sealed class AuthorizationContext
{
    /// <summary>
    /// Creates an evaluation context.
    /// </summary>
    /// <param name="httpContext">The exchange being authorized.</param>
    /// <param name="user">The principal to evaluate the policy against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> or <paramref name="user"/> is <see langword="null"/>.</exception>
    public AuthorizationContext(IHttpContext httpContext, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);

        HttpContext = httpContext;
        User = user;
    }

    /// <summary>
    /// Gets the exchange being authorized.
    /// </summary>
    public IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets the principal the policy is evaluated against. Never <see langword="null"/>; an anonymous
    /// request carries a principal with no authenticated identity.
    /// </summary>
    public ClaimsPrincipal User { get; }
}
