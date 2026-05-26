using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public abstract class ServerTransport : Transport
{
    public sealed override TransportKind Kind { get; } = TransportKind.Server;

}
