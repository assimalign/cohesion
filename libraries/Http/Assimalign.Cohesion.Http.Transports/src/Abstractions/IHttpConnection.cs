using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Represents a protocol-specific HTTP connection layered on top of a transport connection.
/// </summary>
public interface IHttpConnection : ITransportConnection
{
    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <returns>The opened connection context.</returns>
    IHttpConnectionContext Open();

    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened connection context.</returns>
    ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}
