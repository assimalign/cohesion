using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Represents the active HTTP connection context used to receive exchanges and write responses.
/// </summary>
public interface IHttpConnectionContext : ITransportConnectionContext
{
    /// <summary>
    /// Receives HTTP exchanges from the underlying connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for enumeration.</param>
    /// <returns>An asynchronous sequence of received HTTP contexts.</returns>
    IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the response state contained by the supplied HTTP context to the underlying connection.
    /// </summary>
    /// <param name="context">The HTTP context to serialize back to the client.</param>
    /// <param name="cancellationToken">The cancellation token for the write operation.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
