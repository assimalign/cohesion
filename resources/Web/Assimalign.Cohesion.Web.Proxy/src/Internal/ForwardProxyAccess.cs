namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// What the forward proxy's rules allow for one target.
/// </summary>
internal enum ForwardProxyAccess
{
    /// <summary>
    /// No rule matches the target's host and port: the request is answered with 403 Forbidden.
    /// </summary>
    Denied = 0,

    /// <summary>
    /// Only a wildcard pattern matches: the target may not resolve to a loopback, link-local or
    /// unspecified address.
    /// </summary>
    Restricted = 1,

    /// <summary>
    /// An exact pattern matches: the target is reached wherever it resolves.
    /// </summary>
    Open = 2,
}
