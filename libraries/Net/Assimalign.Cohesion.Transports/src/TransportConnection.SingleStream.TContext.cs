using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class SingleStreamTransportConnection<TContext> : TransportConnection<TContext> where TContext : TransportConnectionContext
{
    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public virtual TContext Open() => OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TContext> OpenAsync(CancellationToken cancellationToken = default);
}
