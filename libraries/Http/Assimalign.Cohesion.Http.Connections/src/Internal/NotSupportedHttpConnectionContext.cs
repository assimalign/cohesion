using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A connection context for protocols a listener recognizes but cannot serve: every receive
/// or send fails with <see cref="NotSupportedException"/>. It carries no byte stream — the
/// standalone <see cref="IHttpConnectionContext"/> contract no longer forces one.
/// </summary>
internal sealed class NotSupportedHttpConnectionContext : IHttpConnectionContext
{
    private readonly string _message;

    public NotSupportedHttpConnectionContext(EndPoint? localEndPoint, EndPoint? remoteEndPoint, string message)
    {
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        _message = message;
    }

    public EndPoint? LocalEndPoint { get; }

    public EndPoint? RemoteEndPoint { get; }

    public IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        return ReceiveAsyncCore(cancellationToken);
    }

    private async IAsyncEnumerable<IHttpContext> ReceiveAsyncCore([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        throw new NotSupportedException(_message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(_message);
    }
}
