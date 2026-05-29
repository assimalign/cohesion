using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class SingleStreamTransportConnection : TransportConnection, ISingleStreamTransportConnection
{
    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <returns></returns>
    public abstract TransportConnectionContext Open();

    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default);

    ITransportConnectionContext ISingleStreamTransportConnection.Open()
    {
        return Open();
    }

    async ValueTask<ITransportConnectionContext> ISingleStreamTransportConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
