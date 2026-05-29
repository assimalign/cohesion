using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestServerTransport : ServerTransport
{
    private readonly Queue<TransportConnection> _connections;

    public TestServerTransport(TransportProtocol protocol, IEnumerable<TransportConnection> connections)
    {
        _connections = new Queue<TransportConnection>(connections);
        Protocol = protocol;
    }

    public override TransportProtocol Protocol { get; }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    protected override Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_connections.Count == 0)
        {
            return Task.FromCanceled<TransportConnection>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }

        return Task.FromResult(_connections.Dequeue());
    }
}
