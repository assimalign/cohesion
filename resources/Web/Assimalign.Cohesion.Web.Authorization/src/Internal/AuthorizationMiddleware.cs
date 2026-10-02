using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// The authorization middleware: resolves the effective policy of the request (the endpoint's
/// authorization metadata combined by <see cref="AuthorizationOptions.GetEffectivePolicy"/>, or the
/// fallback policy), establishes the principal through the policy's authentication schemes, evaluates
/// the policy, and either calls <c>next</c> or answers the request with a challenge (no authenticated
/// principal) or a forbid (an authenticated principal the policy rejects) through Web.Authentication.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint comes from the route match <c>UseRouting</c> publishes, so the middleware belongs
/// after <c>UseRouting</c> (and after <c>UseAuthentication</c>, which establishes <c>context.User</c>
/// for policies that name no schemes). Once it has authorized an endpoint, or found that nothing
/// applies to it, the middleware acknowledges the endpoint; routing's dispatch fails an endpoint whose
/// <see cref="AuthorizationMetadata"/> requires authorization and was never acknowledged
/// (<see cref="IRouteMiddlewareMetadata"/>), which is what catches a missing or misordered
/// <c>UseAuthorization</c>.
/// </para>
/// <para>
/// A request with no endpoint (no route matched, or a 405) gets the fallback policy, as does an
/// endpoint without authorization metadata. A CORS preflight is skipped entirely: its candidate
/// endpoint never runs for the preflight, so it is neither authorized nor acknowledged.
/// </para>
/// </remarks>
internal sealed class AuthorizationMiddleware : IWebApplicationMiddleware
{
    /// <summary>
    /// The pipeline verb the middleware acknowledges on the endpoint, and the one
    /// <see cref="AuthorizationMetadata.RequiredMiddleware"/> names.
    /// </summary>
    internal const string Verb = "UseAuthorization";

    private readonly AuthorizationOptions _options;

    // The effective policy of each endpoint, keyed by the endpoint's metadata collection (one stable
    // instance per route). Endpoint metadata is fixed once the route table is built and the options are
    // read-only, so an endpoint's policy is computed once rather than on every request. A null value
    // means the endpoint is not authorized: it allows anonymous access, or it has no authorization
    // metadata and there is no fallback policy. Weak keys keep the cache from holding a route alive.
    private readonly ConditionalWeakTable<IRouterRouteMetadataCollection, AuthorizationPolicy?> _endpointPolicies = new();

    public AuthorizationMiddleware(AuthorizationOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        IRouteMatchFeature? endpoint = context.GetRouteMatch();

        // The candidate endpoint of a CORS preflight never runs for the preflight, which carries no
        // credentials: authorizing it would challenge a request the browser sends without them, and
        // acknowledging it would claim a policy was applied.
        if (endpoint is { IsPreflight: true })
        {
            return next.Invoke(context);
        }

        AuthorizationPolicy? policy = endpoint is null
            ? _options.FallbackPolicy
            : GetEndpointPolicy(endpoint.Metadata);

        return policy is null
            ? ContinueAsync(context, endpoint, next)
            : AuthorizeAsync(context, endpoint, policy, next);
    }

    // Acknowledges every endpoint the middleware processed, with or without a policy of its own. Routing
    // checks the acknowledgment only when the endpoint's last authorization item is a requirement, and
    // such an endpoint always has a policy here; acknowledging the others is harmless.
    private static Task ContinueAsync(IHttpContext context, IRouteMatchFeature? endpoint, WebApplicationMiddleware next)
    {
        if (endpoint is not null)
        {
            context.AcknowledgeEndpointMiddleware(Verb);
        }

        return next.Invoke(context);
    }

    private static async Task AuthorizeAsync(
        IHttpContext context,
        IRouteMatchFeature? endpoint,
        AuthorizationPolicy policy,
        WebApplicationMiddleware next)
    {
        CancellationToken cancellationToken = context.RequestCancelled;
        IReadOnlyList<string> schemes = policy.AuthenticationSchemes;

        ClaimsPrincipal user = schemes.Count == 0
            ? context.User
            : await AuthenticateSchemesAsync(context, schemes, cancellationToken).ConfigureAwait(false);

        if (!await policy.EvaluateAsync(new AuthorizationContext(context, user), cancellationToken).ConfigureAwait(false))
        {
            // RFC 9110 §15.5.2 / §15.5.4: a request without an authenticated principal is asked to
            // authenticate (401, or the scheme's interactive equivalent); an authenticated one the policy
            // rejects is refused (403). The schemes' handlers write the response; the endpoint never runs.
            if (DenyAnonymousRequirement.IsAuthenticated(user))
            {
                await ForbidSchemesAsync(context, schemes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ChallengeSchemesAsync(context, schemes, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await ContinueAsync(context, endpoint, next).ConfigureAwait(false);
    }

    // The combination itself is AuthorizationOptions.GetEffectivePolicy, shared with components that
    // describe endpoints (Web.OpenApi), so a description cannot disagree with what this middleware enforces.
    // The middleware adds only the per-endpoint cache.
    private AuthorizationPolicy? GetEndpointPolicy(IRouterRouteMetadataCollection metadata)
    {
        if (_endpointPolicies.TryGetValue(metadata, out AuthorizationPolicy? policy))
        {
            return policy;
        }

        // Concurrent first requests may both compute; the results are equivalent, and the last write wins.
        // A configuration error (an unknown policy name) throws and is not cached, so every request to
        // the endpoint fails rather than one.
        policy = _options.GetEffectivePolicy(metadata);
        _endpointPolicies.AddOrUpdate(metadata, policy);

        return policy;
    }

    // Authenticates the request with each of the policy's schemes and combines the principals that
    // succeeded, in scheme order. The combined principal replaces context.User so the endpoint sees the
    // principal it was authorized as: a credential only the default scheme accepted (a cookie, say) does
    // not leak into an endpoint that selected other schemes. With no success the principal is anonymous.
    private static async Task<ClaimsPrincipal> AuthenticateSchemesAsync(
        IHttpContext context,
        IReadOnlyList<string> schemes,
        CancellationToken cancellationToken)
    {
        ClaimsPrincipal? first = null;
        ClaimsPrincipal? combined = null;

        for (int i = 0; i < schemes.Count; i++)
        {
            AuthenticateResult result = await context.AuthenticateAsync(schemes[i], cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded || result.Principal is not { } principal)
            {
                continue;
            }

            if (first is null)
            {
                first = principal;
                continue;
            }

            combined ??= new ClaimsPrincipal(first.Identities);
            combined.AddIdentities(principal.Identities);
        }

        ClaimsPrincipal user = combined ?? first ?? new ClaimsPrincipal(new ClaimsIdentity());
        context.User = user;

        return user;
    }

    private static async Task ChallengeSchemesAsync(IHttpContext context, IReadOnlyList<string> schemes, CancellationToken cancellationToken)
    {
        if (schemes.Count == 0)
        {
            await context.ChallengeAsync(scheme: null, properties: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (int i = 0; i < schemes.Count; i++)
        {
            await context.ChallengeAsync(schemes[i], properties: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ForbidSchemesAsync(IHttpContext context, IReadOnlyList<string> schemes, CancellationToken cancellationToken)
    {
        if (schemes.Count == 0)
        {
            await context.ForbidAsync(scheme: null, properties: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (int i = 0; i < schemes.Count; i++)
        {
            await context.ForbidAsync(schemes[i], properties: null, cancellationToken).ConfigureAwait(false);
        }
    }
}
