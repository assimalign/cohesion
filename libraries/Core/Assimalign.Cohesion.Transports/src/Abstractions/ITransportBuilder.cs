using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// An interface for building a chain of <see cref="ITransportMiddleware"/>.
/// </summary>
public interface ITransportBuilder
{
    /// <summary>
    /// Adds a middleware to the chain.
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    ITransportBuilder Use(Func<TransportMiddleware, TransportMiddleware> middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ITransport Build();
}