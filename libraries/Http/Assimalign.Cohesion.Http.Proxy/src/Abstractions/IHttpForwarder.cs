using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Forwards an HTTP exchange to another server and relays its response.
/// </summary>
/// <remarks>
/// A forwarder is the mechanism shared by a reverse proxy, which chooses the destination from its
/// routes, and by a forward proxy, which takes it from an absolute-form request target. Deciding
/// which requests to forward, and where, is the caller's job.
/// </remarks>
public interface IHttpForwarder
{
    /// <summary>
    /// Forwards the exchange's request to <paramref name="destination"/> and relays the response,
    /// an upgraded connection, or the failure, to the client.
    /// </summary>
    /// <param name="context">The exchange to forward.</param>
    /// <param name="destination">
    /// The complete absolute <c>http</c> or <c>https</c> URI of the forwarded request, path and query
    /// included; <see cref="HttpForwarder.CreateDestination"/> builds one from a destination prefix.
    /// </param>
    /// <param name="cancellationToken">A token that stops forwarding. The exchange's own
    /// <see cref="IHttpContext.RequestCancelled"/> token also stops it.</param>
    /// <returns>
    /// How the exchange ended. The client has already been answered, so the caller writes nothing
    /// more.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="destination"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is not an absolute <c>http</c> or <c>https</c> URI.
    /// </exception>
    Task<HttpProxyResult> ForwardAsync(IHttpContext context, Uri destination, CancellationToken cancellationToken = default);
}
