using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestMultiplexTransportConnection : MultiplexTransportConnection
{
    private readonly Queue<TestTransportConnectionContext> _inboundContexts;
    private ConnectionState _state;

    public TestMultiplexTransportConnection(IEnumerable<TestTransportConnectionContext> inboundContexts, TransportProtocol protocol)
    {
        _inboundContexts = new Queue<TestTransportConnectionContext>(inboundContexts);
        Protocol = protocol;
        Id = ConnectionId.New();
        TransportId = TransportId.New();
        _state = ConnectionState.Idle;
    }

    public override ConnectionId Id { get; }

    public override TransportId TransportId { get; }

    public override TransportProtocol Protocol { get; }

    public override ConnectionState State => _state;

    public override void Abort()
    {
        _state = ConnectionState.Aborted;
    }

    public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        Abort();
        return ValueTask.CompletedTask;
    }

    public override void Dispose()
    {
        _state = ConnectionState.Closed;
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public override TransportConnectionContext OpenInbound()
    {
        _state = ConnectionState.Open;
        return DequeueInbound();
    }

    public override ValueTask<TransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default)
    {
        if (_inboundContexts.Count == 0)
        {
            return ValueTask.FromException<TransportConnectionContext>(new OperationCanceledException(cancellationToken));
        }

        return ValueTask.FromResult<TransportConnectionContext>(OpenInbound());
    }

    public override TransportConnectionContext OpenOutbound()
    {
        return new TestTransportConnectionContext(Array.Empty<byte>());
    }

    public override ValueTask<TransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<TransportConnectionContext>(OpenOutbound());
    }

    private TransportConnectionContext DequeueInbound()
    {
        return _inboundContexts.Dequeue();
    }
}
