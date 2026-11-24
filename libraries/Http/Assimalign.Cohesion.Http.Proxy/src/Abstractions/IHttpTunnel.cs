using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Serves an HTTP/1.1 <c>CONNECT</c> request (RFC 9110 §9.3.6) by relaying bytes between the client
/// and a destination.
/// </summary>
/// <remarks>
/// A tunnel only moves bytes: it cannot see or change what flows through it, which is usually TLS
/// between the client and the destination. Deciding which destinations a client may reach is the
/// caller's job.
/// </remarks>
public interface IHttpTunnel
{
    /// <summary>
    /// Connects to <paramref name="destination"/>, accepts the client's <c>CONNECT</c> with 200, and
    /// relays bytes in both directions until either side closes.
    /// </summary>
    /// <param name="context">The exchange whose request is the <c>CONNECT</c>.</param>
    /// <param name="destination">The host and port to connect to.</param>
    /// <param name="cancellationToken">A token that closes the tunnel. The exchange's own
    /// <see cref="IHttpContext.RequestCancelled"/> token also closes it.</param>
    /// <returns>
    /// How the tunnel ended. The client has already been answered, so the caller writes nothing more.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// </exception>
    Task<HttpProxyResult> RelayAsync(IHttpContext context, DnsEndPoint destination, CancellationToken cancellationToken = default);
}
