using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP/1.1 connection transition (protocol upgrade or CONNECT tunnel)
/// available on the current exchange.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are produced by this package's interceptor pair
/// (<see cref="HttpProtocolUpgrade.CreateRequestInterceptor"/> /
/// <see cref="HttpProtocolUpgrade.CreateResponseInterceptor"/>, registered on the server
/// transport's listener options) when an incoming request matches the wire-level conditions for
/// a transition (RFC 9110 §7.8 for <c>Upgrade</c>, RFC 9110 §9.3.6 + RFC 9112 §3.2.3 for
/// <c>CONNECT</c>). Application code obtains the capability from <c>context.Upgrade</c>:
/// </para>
/// <code>
/// if (context.Upgrade is { Kind: HttpProtocolUpgradeKind.Upgrade } upgrade)
/// {
///     Stream tunnel = await upgrade.AcceptAsync(context.RequestCancelled);
///     // ...use tunnel...
/// }
/// </code>
/// <para>
/// Calling <see cref="AcceptAsync"/> writes the appropriate status line
/// (<c>101 Switching Protocols</c> for an Upgrade, <c>200 OK</c> for a CONNECT) and
/// any required <c>Connection</c> / <c>Upgrade</c> response headers, then surrenders
/// the underlying transport stream to the caller. Acceptance exercises the transport's
/// exchange control's takeover (<see cref="IHttpExchangeControl.TakeOver"/>), so the regular
/// response pipeline is suppressed — the transport's send for the same exchange becomes a
/// no-op and the connection leaves the keep-alive request loop.
/// </para>
/// </remarks>
public interface IHttpProtocolUpgrade
{
    /// <summary>
    /// Gets the kind of transition the request is asking for.
    /// </summary>
    HttpProtocolUpgradeKind Kind { get; }

    /// <summary>
    /// Gets the protocol token requested for an <see cref="HttpProtocolUpgradeKind.Upgrade"/>
    /// transition (for example <c>"websocket"</c>). Returns <see langword="null"/>
    /// for <see cref="HttpProtocolUpgradeKind.Connect"/>.
    /// </summary>
    string? Protocol { get; }

    /// <summary>
    /// Accepts the transition. Writes the appropriate status response and returns
    /// the underlying duplex transport stream so the caller can read and write
    /// raw bytes for the negotiated protocol or tunneled connection.
    /// </summary>
    /// <param name="cancellationToken">A token that signals when acceptance should
    /// be cancelled.</param>
    /// <returns>The duplex transport stream. The caller assumes ownership and is
    /// responsible for closing it when the transition completes.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when the
    /// transition has already been accepted on this exchange, or when the exchange can no longer
    /// be taken over — the final response has started or the exchange was aborted
    /// (<see cref="IHttpExchangeControl.TakeOver"/> guards the claim).</exception>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default);
}
