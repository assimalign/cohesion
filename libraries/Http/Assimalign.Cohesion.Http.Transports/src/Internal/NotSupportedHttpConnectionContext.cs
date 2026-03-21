using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal sealed class NotSupportedHttpConnectionContext : IHttpConnectionContext
{
    private static readonly ITransportConnectionPipe DisabledPipe = new TransportConnectionPipe(Stream.Null);
    private static readonly IDictionary<string, object?> EmptyItems = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private readonly string _message;

    public NotSupportedHttpConnectionContext(EndPoint? localEndPoint, EndPoint? remoteEndPoint, string message)
    {
        LocalEndPoint = localEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        _message = message;
    }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    public ITransportConnectionPipe Pipe => DisabledPipe;

    public IDictionary<string, object?> Items => EmptyItems;

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
