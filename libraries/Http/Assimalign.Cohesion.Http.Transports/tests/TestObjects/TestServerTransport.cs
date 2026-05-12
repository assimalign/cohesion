using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestServerTransport : ITransport
{
    private readonly Queue<ITransportConnection> _connections;

    public TestServerTransport(TransportProtocol protocol, IEnumerable<ITransportConnection> connections)
    {
        _connections = new Queue<ITransportConnection>(connections);
        Protocol = protocol;
        Id = TransportId.New();
    }

    public TransportId Id { get; }

    public TransportKind Kind => TransportKind.Server;

    public TransportProtocol Protocol { get; }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ITransportConnection Initialize()
    {
        return InitializeAsync().GetAwaiter().GetResult();
    }

    public Task<ITransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_connections.Count == 0)
        {
            return Task.FromCanceled<ITransportConnection>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }

        return Task.FromResult(_connections.Dequeue());
    }
}
