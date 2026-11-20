using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Two cross-wired in-memory connections, for the SASL mechanisms that take the connection they
/// authenticate. Disposing the pair disposes both ends.
/// </summary>
internal sealed class TestConnectionPair : IAsyncDisposable
{
    public TestConnectionPair()
    {
        (Client, Server) = InMemoryConnectionPair.Create();
    }

    public DuplexConnection Client { get; }

    public DuplexConnection Server { get; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}
