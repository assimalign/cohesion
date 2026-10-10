using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// A <see cref="ConnectionListener"/> double whose accept waits until a connection is enqueued, as a real
/// listener waits for a peer, and which counts the accepts it has started.
/// </summary>
internal sealed class BlockingConnectionListener : ConnectionListener
{
    private readonly Channel<Connection> _pending = Channel.CreateUnbounded<Connection>();
    private int _acceptCalls;

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 22001);

    public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

    /// <summary>Gets how many accepts have started, including one still waiting.</summary>
    public int AcceptCalls => Volatile.Read(ref _acceptCalls);

    /// <summary>Queues a connection for the next accept.</summary>
    public void Enqueue(Connection connection) => _pending.Writer.TryWrite(connection);

    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acceptCalls);

        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(BlockingConnectionListener));
        }
    }

    public override ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }
}
