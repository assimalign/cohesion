using System;

namespace Assimalign.Cohesion.Transports;

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
