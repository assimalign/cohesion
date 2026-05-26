using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportConnection<TContext> : TransportConnection where TContext : TransportConnectionContext
{
    /// <summary>
    /// 
    /// </summary>
    protected new abstract TransportPipeline<TContext>? Pipeline { get; }
}
