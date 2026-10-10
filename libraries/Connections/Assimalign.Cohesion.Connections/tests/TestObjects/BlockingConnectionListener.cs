using System;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A <see cref="ConnectionListener"/> double whose accept waits until a connection is enqueued, as a real
/// listener waits for a peer, and which can be made to fail as a real listener's endpoint can.
/// </summary>
internal sealed class BlockingConnectionListener : ConnectionListener
{
    private readonly Channel<Connection> _pending = Channel.CreateUnbounded<Connection>();
    private int _acceptCalls;
    private int _isDisposed;

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 15001);

    public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities;

    /// <summary>Gets how many accepts have started, including one still waiting.</summary>
    public int AcceptCalls => Volatile.Read(ref _acceptCalls);

    /// <summary>Gets whether the listener has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    /// <summary>Queues a connection for the next accept.</summary>
    public void Enqueue(Connection connection) => _pending.Writer.TryWrite(connection);

    /// <summary>Fails every accept with <paramref name="exception"/> once the queued connections are taken.</summary>
    public void Fault(Exception exception) => _pending.Writer.TryComplete(exception);

    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acceptCalls);

        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(BlockingConnectionListener));
        }
    }

    public override ValueTask DisposeAsync()
    {
        Volatile.Write(ref _isDisposed, 1);
        _pending.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }
}
