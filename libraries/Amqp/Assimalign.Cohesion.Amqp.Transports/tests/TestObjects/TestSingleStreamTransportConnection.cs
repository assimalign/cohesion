using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Tests.TestObjects;

internal sealed class TestSingleStreamTransportConnection : ISingleStreamTransportConnection
{
    private readonly TestTransportConnectionContext _context;

    public TestSingleStreamTransportConnection(TestTransportConnectionContext context, TransportProtocol protocol)
    {
        _context = context;
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

    public ITransportConnectionContext Open()
    {
        State = ConnectionState.Open;
        return _context;
    }

    public ValueTask<ITransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Open());
    }
}
