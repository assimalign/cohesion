using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents a transport pipeline middleware delegate.
/// </summary>
/// <param name="connection">The active transport connection.</param>
/// <param name="context">The transport connection context for the current execution.</param>
/// <param name="cancellationToken">A token that signals the transport operation should be canceled.</param>
/// <returns>A task that represents the middleware execution.</returns>
public delegate Task TransportMiddleware(
    ITransportConnection connection,
    ITransportConnectionContext context,
    CancellationToken cancellationToken);
