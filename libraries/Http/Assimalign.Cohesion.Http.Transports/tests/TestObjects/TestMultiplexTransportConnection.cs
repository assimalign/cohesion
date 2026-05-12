using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestMultiplexTransportConnection : IMultiplexTransportConnection
{
    private readonly Queue<TestTransportConnectionContext> _inboundContexts;

    public TestMultiplexTransportConnection(IEnumerable<TestTransportConnectionContext> inboundContexts, TransportProtocol protocol)
    {
        _inboundContexts = new Queue<TestTransportConnectionContext>(inboundContexts);
        Protocol = protocol;
        Id = ConnectionId.New();
        TransportId = TransportId.New();
        State = ConnectionState.Idle;
    }

    public ConnectionId Id { get; }

    public TransportId TransportId { get; }

    public TransportProtocol Protocol { get; }

    public ConnectionState State { get; private set; }

    public void Abort()
    {
        State = ConnectionState.Aborted;
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        Abort();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        State = ConnectionState.Closed;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public ITransportConnectionContext OpenInbound()
    {
        State = ConnectionState.Open;
        return DequeueInbound();
    }

    public ValueTask<ITransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default)
    {
        if (_inboundContexts.Count == 0)
        {
            return ValueTask.FromException<ITransportConnectionContext>(new OperationCanceledException(cancellationToken));
        }

        return ValueTask.FromResult(OpenInbound());
    }

    public ITransportConnectionContext OpenOutbound()
    {
        return new TestTransportConnectionContext(System.Array.Empty<byte>());
    }

    public ValueTask<ITransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(OpenOutbound());
    }

    private ITransportConnectionContext DequeueInbound()
    {
        return _inboundContexts.Dequeue();
    }
}
