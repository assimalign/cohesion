namespace Assimalign.Cohesion.Net.Transports;

/// <summary>
/// An interface for building a chain of <see cref="ITransportMiddleware"/>.
/// </summary>
public interface ITransportMiddlewareBuilder
{
    /// <summary>
    /// Adds a middleware to the chain.
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    ITransportMiddlewareBuilder UseNext(ITransportMiddleware middleware);
    /// <summary>
    /// Adds a fluent middleware to the chain.
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    ITransportMiddlewareBuilder UseNext(TransportMiddleware middleware);

    /// <summary>
    /// Builds the chain of <see cref="ITransportMiddleware"/> and returns the handler.
    /// </summary>
    /// <returns></returns>
    TransportMiddlewareHandler Build();
}