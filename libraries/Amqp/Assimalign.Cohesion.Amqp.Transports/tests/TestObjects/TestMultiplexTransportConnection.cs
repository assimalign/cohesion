using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Tests.TestObjects;

internal sealed class TestMultiplexTransportConnection : IMultiplexTransportConnection
{
    private readonly TestTransportConnectionContext _inboundContext;
    private readonly TestTransportConnectionContext _outboundContext;

    public TestMultiplexTransportConnection(
        TestTransportConnectionContext inboundContext,
        TestTransportConnectionContext outboundContext,
        TransportProtocol protocol)
    {
        _inboundContext = inboundContext;
        _outboundContext = outboundContext;
        Protocol = protocol;
        Id = ConnectionId.New();
        TransportId = TransportId.New();
        State = ConnectionState.Idle;
    }

    public ConnectionId Id { get; }

    public TransportId TransportId { get; }

    public TransportProtocol Protocol { get; }

    public ConnectionState State { get; private set; }

    public int InboundOpenCount { get; private set; }

    public int OutboundOpenCount { get; private set; }

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
        InboundOpenCount++;
        return _inboundContext;
    }

    public ValueTask<ITransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(OpenInbound());
    }

    public ITransportConnectionContext OpenOutbound()
    {
        State = ConnectionState.Open;
        OutboundOpenCount++;
        return _outboundContext;
    }

    public ValueTask<ITransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(OpenOutbound());
    }
}
