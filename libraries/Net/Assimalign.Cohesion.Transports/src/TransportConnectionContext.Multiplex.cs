using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnectionContext : TransportConnectionContext, IMultiplexTransportConnectionContext
{
    /// <summary>
    /// 
    /// </summary>
    public abstract bool IsBidirectional { get; }
}
