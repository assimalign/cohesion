using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestSingleStreamTransportConnection : SingleStreamTransportConnection
{
    private readonly TestTransportConnectionContext _context;
    private ConnectionState _state;

    public TestSingleStreamTransportConnection(TestTransportConnectionContext context, TransportProtocol protocol)
    {
        _context = context;
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

    public override TransportConnectionContext Open()
    {
        _state = ConnectionState.Open;
        return _context;
    }

    public override ValueTask<TransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<TransportConnectionContext>(Open());
    }
}
