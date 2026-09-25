using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Serves as the base for protocol-specific HTTP connection contexts.
/// </summary>
public abstract class HttpConnectionContext : IHttpConnectionContext
{
    /// <inheritdoc />
    public abstract EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
