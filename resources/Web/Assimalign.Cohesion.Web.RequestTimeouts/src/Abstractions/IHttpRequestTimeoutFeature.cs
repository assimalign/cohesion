using System;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// The per-exchange feature the request-timeout middleware installs on
/// <see cref="IHttpContext.Features"/>, through which a handler can observe, disable, or re-arm
/// the timeout governing its exchange (parity with ASP.NET's <c>IHttpRequestTimeoutFeature</c> /
/// <c>DisableRequestTimeout</c>).
/// </summary>
/// <remarks>
/// <para>
/// Extensibility stays on the feature-collection seam rather than attributes or reflection: the
/// middleware attaches this feature to every exchange it governs, and downstream code resolves it
/// with <c>context.Features.Get&lt;IHttpRequestTimeoutFeature&gt;()</c>. When the middleware is
/// not registered (or is suspended for a debugger), no feature is present and the lookup returns
/// <see langword="null"/>.
/// </para>
/// <para>
/// <see cref="Disable"/> and <see cref="SetTimeout"/> take effect only while the timeout has not
/// fired yet — once <see cref="Token"/> is cancelled the exchange is already being timed out and
/// both are no-ops. This is the same inherent race ASP.NET documents for
/// <c>DisableRequestTimeout</c>; call them early in the handler.
/// </para>
/// </remarks>
public interface IHttpRequestTimeoutFeature : IHttpFeature
{
    /// <summary>
    /// Gets the effective request-cancellation token for the exchange: it is cancelled when the
    /// timeout expires <em>or</em> when the underlying request is cancelled (client abort,
    /// connection teardown). Downstream of the middleware this is the same token
    /// <see cref="IHttpContext.RequestCancelled"/> reports.
    /// </summary>
    CancellationToken Token { get; }

    /// <summary>
    /// Disables the timeout for this exchange, letting the handler run for as long as the
    /// underlying request lives. A no-op when the timeout has already fired.
    /// </summary>
    void Disable();

    /// <summary>
    /// Re-arms the timeout to fire <paramref name="timeout"/> from now, replacing whatever
    /// deadline the effective policy armed. A no-op when the timeout has already fired.
    /// </summary>
    /// <param name="timeout">The new interval, measured from the moment of the call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero or negative.</exception>
    void SetTimeout(TimeSpan timeout);
}
