using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Defines a builder for configuring and constructing a transport pipeline by adding middleware components.
/// </summary>
/// <remarks>
/// Implementations of this interface allow for the composition of middleware in a transport pipeline,
/// enabling customization of message processing behavior. The builder pattern facilitates fluent configuration before
/// creating the final pipeline instance.
/// </remarks>
public interface ITransportPipelineBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    ITransportPipelineBuilder Use(Func<TransportMiddleware, TransportMiddleware> middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ITransportPipeline Build();
}
