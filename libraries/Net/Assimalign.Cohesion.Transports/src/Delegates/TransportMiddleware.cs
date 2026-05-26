using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents a transport pipeline middleware delegate.
/// </summary>
/// <param name="context">The transport connection context for the current execution.</param>
/// <returns>A task that represents the middleware execution.</returns>
public delegate Task TransportMiddleware(ITransportConnectionContext context);
