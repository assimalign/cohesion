using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public abstract class ClientTransport : Transport
{
    public sealed override TransportKind Kind { get; } = TransportKind.Client;
}
