using System;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// An <see cref="IConnectionListener"/> that applies a <see cref="IConnectionLayer"/> to every
/// accepted connection, upgrading each one on its own task.
/// </summary>
/// <remarks>
/// <para>
/// The first <see cref="AcceptAsync(CancellationToken)"/> starts a pump that accepts connections from the
/// inner listener and hands each one to the thread pool to upgrade, then accepts the next without waiting
/// for it. A peer that is slow, silent, or sends bytes the layer rejects therefore delays only its own
/// connection. <see cref="AcceptAsync(CancellationToken)"/> returns the connections whose upgrade has
/// completed, in the order they complete.
/// </para>
/// <para>
/// A connection holds one of <c>maxConcurrentUpgrades</c> slots from the moment the pump accepts it until
/// <see cref="AcceptAsync(CancellationToken)"/> returns it or its upgrade fails. With every slot taken the
/// pump stops accepting, so further peers wait in the inner listener's own backlog and a flood of stalled
/// upgrades holds at most that many connections.
/// </para>
/// <para>
/// An upgrade that throws (a failed or timed-out handshake) belongs to its connection, not to the
/// listener: the connection is disposed, the failure is reported through
/// <see cref="ConnectionLayerEventSource"/>, and the slot is freed. Only the listener's own failure reaches
/// <see cref="AcceptAsync(CancellationToken)"/>: when the inner listener's accept faults, the connections
/// still upgrading finish and are returned first, then its exception is rethrown; after disposal it throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
internal sealed class LayeredConnectionListener : IConnectionListener
{
    /// <summary>The number of slots <c>listener.Use(layer)</c> gives the listener.</summary>
    internal const int DefaultMaxConcurrentUpgrades = 512;

    private readonly IConnectionListener _inner;
    private readonly IConnectionLayer _layer;
    private readonly SemaphoreSlim _slots;
    private readonly Channel<IConnection> _upgraded;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private readonly TaskCompletionSource _upgradesDrained;
    private readonly Lock _gate;
    private Task? _pump;
    private int _pendingUpgrades;
    private int _draining;
    private bool _isDisposed;

    public LayeredConnectionListener(IConnectionListener inner, IConnectionLayer layer, int maxConcurrentUpgrades)
    {
        _inner = inner;
        _layer = layer;
        _slots = new SemaphoreSlim(maxConcurrentUpgrades, maxConcurrentUpgrades);

        // Unbounded because the slots already bound it: a queued connection keeps its slot until
        // AcceptAsync returns it.
        _upgraded = Channel.CreateUnbounded<IConnection>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        _lifetime = new CancellationTokenSource();
        _lifetimeToken = _lifetime.Token;
        _upgradesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = new Lock();
    }

    public EndPoint EndPoint => _inner.EndPoint;

    public ConnectionCapabilities Capabilities => _layer.Describe(_inner.Capabilities);

    public ValueTask BindAsync(CancellationToken cancellationToken = default)
        => _inner.BindAsync(cancellationToken);

    public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        EnsurePumpStarted();

        try
        {
            // The token abandons this wait only: connections the pump accepted keep upgrading and are
            // returned by a later call.
            IConnection connection = await _upgraded.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            // The connection leaves the listener here, so its slot goes back to the pump.
            _slots.Release();

            return connection;
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            // The inner listener faulted; every connection upgraded before the fault has been returned.
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pump;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pump = _pump;
        }

        // Cancel before releasing the inner listener: the pump's accept stops, and every upgrade in
        // flight is canceled and releases its own connection.
        _lifetime.Cancel();

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (pump is not null)
            {
                await pump.ConfigureAwait(false);
            }

            await WhenUpgradesDrainedAsync().ConfigureAwait(false);

            // Nothing writes any more. Release the upgraded connections AcceptAsync never returned, and
            // let a pending or later AcceptAsync observe the disposal.
            _upgraded.Writer.TryComplete();

            while (_upgraded.Reader.TryRead(out IConnection? connection))
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }

            _lifetime.Dispose();
        }
    }

    private void EnsurePumpStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_pump is not null)
            {
                return;
            }

            // The pump outlives the AcceptAsync call that starts it, so it must not capture that caller's
            // execution context: an ambient activity would otherwise parent every later upgrade.
            if (ExecutionContext.IsFlowSuppressed())
            {
                _pump = Task.Run(PumpAsync);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _pump = Task.Run(PumpAsync);
                }
            }
        }
    }

    private async Task PumpAsync()
    {
        Exception? fault = null;

        try
        {
            while (true)
            {
                await _slots.WaitAsync(_lifetimeToken).ConfigureAwait(false);

                IConnection connection;

                try
                {
                    connection = await _inner.AcceptAsync(_lifetimeToken).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

                // Upgrade on the thread pool and go straight back to accepting. Even the synchronous part
                // of an upgrade stays off this loop: a TLS handshake that finds the client's first flight
                // already buffered signs its reply before it first yields.
                Interlocked.Increment(ref _pendingUpgrades);
                ConnectionLayerEventSource.Log.UpgradeStarted();
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state => _ = state.Listener.RunUpgradeAsync(state.Connection),
                    (Listener: this, Connection: connection),
                    preferLocal: false);
            }
        }
        catch (Exception) when (_lifetimeToken.IsCancellationRequested)
        {
            // Disposal ended the accept, as a cancellation or as the inner listener reporting its own
            // disposal; DisposeAsync releases everything still held.
        }
        catch (Exception exception)
        {
            // Any exception from the inner accept means it can produce no more connections: it faulted,
            // or something disposed it underneath this listener. That is the listener's own failure, so
            // AcceptAsync surfaces it.
            fault = exception;
        }

        if (fault is not null)
        {
            // Connections accepted before the fault are still upgrading; let them finish and queue, so a
            // consumer receives them before it receives the fault.
            await WhenUpgradesDrainedAsync().ConfigureAwait(false);
            _upgraded.Writer.TryComplete(fault);
        }
    }

    private async Task RunUpgradeAsync(IConnection connection)
    {
        try
        {
            IConnection upgraded;

            try
            {
                upgraded = await _layer.UpgradeAsync(connection, _lifetimeToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Whatever the layer throws (a failed handshake, a handshake its timeout canceled, a peer
                // that hung up mid-way) belongs to this connection alone, so it ends this connection and
                // never the listener (#1304). A failed layer leaves the connection to its caller. A
                // cancellation caused by disposal is not a failure to report.
                if (!_lifetimeToken.IsCancellationRequested)
                {
                    ConnectionLayerEventSource.Log.UpgradeFailed(connection.Id, connection.RemoteEndPoint, exception);
                }

                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
                _slots.Release();

                return;
            }

            if (!_upgraded.Writer.TryWrite(upgraded))
            {
                // Nothing will return the connection any more.
                await DisposeQuietlyAsync(upgraded).ConfigureAwait(false);
                _slots.Release();
            }
        }
        finally
        {
            ConnectionLayerEventSource.Log.UpgradeFinished();

            if (Interlocked.Decrement(ref _pendingUpgrades) == 0 && Volatile.Read(ref _draining) == 1)
            {
                _upgradesDrained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Completes once every upgrade the pump started has finished. Called only after the pump has stopped
    /// starting upgrades: by the pump itself when its inner accept faults, or by disposal once the pump
    /// has returned.
    /// </summary>
    private Task WhenUpgradesDrainedAsync()
    {
        // Publish the drain before reading the count, as an upgrade publishes its decrement before reading
        // the drain flag: whichever side runs second sees the other's write, so the signal is never lost.
        Interlocked.Exchange(ref _draining, 1);

        if (Volatile.Read(ref _pendingUpgrades) == 0)
        {
            _upgradesDrained.TrySetResult();
        }

        return _upgradesDrained.Task;
    }

    private static async ValueTask DisposeQuietlyAsync(IConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Releasing a connection must not fail the listener; whatever ended the connection has
            // already been reported.
        }
    }
}
