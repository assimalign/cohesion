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
/// Implementations of this interface are produced by the transport when an incoming
/// request matches the wire-level conditions for a transition (RFC 9110 §7.8 for
/// <c>Upgrade</c>, RFC 9110 §9.3.6 + RFC 9112 §3.2.3 for <c>CONNECT</c>). Application
/// code obtains the feature from <see cref="IHttpContext.Upgrade"/>:
/// </para>
/// <code>
/// if (context.Upgrade is { Kind: HttpProtocolUpgradeKind.Upgrade } upgrade)
/// {
///     Stream tunnel = await upgrade.AcceptAsync(context.RequestAborted);
///     // ...use tunnel...
/// }
/// </code>
/// <para>
/// Calling <see cref="AcceptAsync"/> writes the appropriate status line
/// (<c>101 Switching Protocols</c> for an Upgrade, <c>200 OK</c> for a CONNECT) and
/// any required <c>Connection</c> / <c>Upgrade</c> response headers, then surrenders
/// the underlying transport stream to the caller. After acceptance, the regular
/// response pipeline is suppressed — calling
/// <see cref="IHttpConnectionContext.SendAsync"/> for the same exchange becomes a
/// no-op so the framework does not double-write the response.
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
    /// transition has already been accepted on this exchange.</exception>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default);
}
