using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class ClientTransport<TConnection> : ClientTransport where TConnection : TransportConnection
{
    public abstract Task<TConnection> ConnectAsync(CancellationToken cancellationToken = default);
    protected sealed override async Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken) => await ConnectAsync(cancellationToken);
}
