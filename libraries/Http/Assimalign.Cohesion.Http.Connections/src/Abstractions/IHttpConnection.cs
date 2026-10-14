using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Represents a protocol-specific HTTP connection layered on top of an accepted transport connection.
/// </summary>
/// <remarks>
/// Identity, endpoints, capabilities, features and lifetime are those of the wrapped transport
/// connection. The feature collection is the same collection exposed by that transport.
/// </remarks>
public interface IHttpConnection : IConnection
{
    /// <summary>
    /// Gets the single HTTP protocol spoken by this connection.
    /// </summary>
    HttpProtocol Protocol { get; }

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
