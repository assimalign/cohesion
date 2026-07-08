namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Describes the outcome of evaluating an HTTP request against an <see cref="IRouter"/>.
/// </summary>
public enum RouteMatchStatus
{
    /// <summary>
    /// No route matched the request path. Callers should treat this as a candidate for a 404 response.
    /// </summary>
    NoMatch = 0,

    /// <summary>
    /// A route matched both the request path and the request method.
    /// </summary>
    Matched = 1,

    /// <summary>
    /// One or more routes matched the request path, but none accepted the request method.
    /// Callers should treat this as a 405 response and emit an <c>Allow</c> header.
    /// </summary>
    MethodNotAllowed = 2,
}
