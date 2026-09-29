using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>
/// Dispatches requests over the fixed <c>/cohesion/v1</c> route table.
/// </summary>
/// <remarks>
/// This replaces the former <c>Web.Routing</c> dependency: a library may not reference a
/// <c>resources/**</c> package, and five fixed templates need no general router. Behaviour is the
/// subset of <c>Web.Routing</c> those templates exercised. The request path is trimmed of
/// <c>/</c> and split without empty segments; routes are tried in table order; the first route
/// whose path matches and whose method is accepted wins; a <c>HEAD</c> request is served by a
/// <c>GET</c> route; a path that matched only with other methods returns <c>405</c> with an
/// <c>Allow</c> header listing those methods in route order (plus <c>HEAD</c> when <c>GET</c> is
/// listed); and anything else returns <c>404</c>. Neither status writes a body.
/// </remarks>
internal sealed class ControlPlaneRouter
{
    private readonly ControlPlaneRoute[] _routes;

    /// <summary>Creates a router that tries <paramref name="routes"/> in the given order.</summary>
    /// <param name="routes">The route table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> or an entry is <see langword="null"/>.</exception>
    public ControlPlaneRouter(params ControlPlaneRoute[] routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        for (int index = 0; index < routes.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(routes[index], nameof(routes));
        }

        _routes = (ControlPlaneRoute[])routes.Clone();
    }

    /// <summary>Matches a request method and path against the route table.</summary>
    /// <param name="method">The request method.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The match outcome.</returns>
    public ControlPlaneRouteMatch Match(HttpMethod method, HttpPath path)
    {
        string[] segments = SplitPath(path);
        List<HttpMethod>? allowed = null;
        for (int index = 0; index < _routes.Length; index++)
        {
            ControlPlaneRoute route = _routes[index];
            if (!route.TryMatchPath(segments, out IReadOnlyDictionary<string, string> values))
            {
                continue;
            }

            if (AcceptsMethod(route.Method, method))
            {
                return ControlPlaneRouteMatch.Matched(route, values);
            }

            allowed ??= new List<HttpMethod>();
            if (!allowed.Contains(route.Method))
            {
                allowed.Add(route.Method);
            }
        }

        if (allowed is null)
        {
            return ControlPlaneRouteMatch.Unmatched;
        }

        // A GET-capable path also answers HEAD, so the Allow header advertises it.
        if (allowed.Contains(HttpMethod.Get) && !allowed.Contains(HttpMethod.Head))
        {
            allowed.Add(HttpMethod.Head);
        }

        return ControlPlaneRouteMatch.MethodNotAllowed(allowed);
    }

    /// <summary>Routes a request: invokes the matched handler, or writes <c>404</c> or <c>405</c>.</summary>
    /// <param name="context">The request being served.</param>
    /// <param name="cancellationToken">Cancels request handling.</param>
    /// <returns>A task that completes when the response is prepared.</returns>
    public Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        ControlPlaneRouteMatch match = Match(context.Request.Method, context.Request.Path);
        switch (match.Status)
        {
            case ControlPlaneRouteMatchStatus.Matched:
                return match.Route!.Handler(context, match.Values, cancellationToken);

            case ControlPlaneRouteMatchStatus.MethodNotAllowed:
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                context.Response.Headers[HttpHeaderKey.Allow] = FormatAllowHeader(match.AllowedMethods);
                return Task.CompletedTask;

            default:
                context.Response.StatusCode = HttpStatusCode.NotFound;
                return Task.CompletedTask;
        }
    }

    // RFC 9110 §9.3.2: HEAD is served identically to GET, so a GET route accepts it.
    private static bool AcceptsMethod(HttpMethod accepted, HttpMethod method) =>
        accepted == method || (method == HttpMethod.Head && accepted == HttpMethod.Get);

    private static string FormatAllowHeader(IReadOnlyList<HttpMethod> methods)
    {
        var tokens = new string[methods.Count];
        for (int index = 0; index < tokens.Length; index++)
        {
            tokens[index] = methods[index].Value;
        }

        return string.Join(", ", tokens);
    }

    private static string[] SplitPath(HttpPath path)
    {
        string value = path.Value.Trim('/');
        return value.Length == 0
            ? Array.Empty<string>()
            : value.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
