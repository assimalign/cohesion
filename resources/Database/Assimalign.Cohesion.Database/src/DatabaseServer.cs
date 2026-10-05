using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every database server: the network front-end for one engine, which accepts
/// connections, authenticates sessions and pumps protocol frames into engine sessions.
/// </summary>
/// <remarks>
/// <para>
/// Servers are <b>per-model</b>: each fronts exactly one engine (<see cref="Engine"/>), fixed by
/// the protected constructor, and each model implements its wire machinery inside its own
/// package. "Running" lives here, not on the engine: an engine is a data machine, and the server
/// is the thing that starts and stops.
/// </para>
/// <para>
/// <b>The lifecycle is one state machine the base owns</b>, as the four model servers each carried
/// it before the bases: a server is created inert; <see cref="StartAsync"/> starts it once (a
/// second start while it runs returns), and a start that fails leaves it stopped for good;
/// <see cref="StopAsync"/> is terminal and idempotent, and stops a server that never started as
/// well, so the leaf releases what it owns either way; a start after a stop throws
/// <see cref="ObjectDisposedException"/>. One gate serializes start and stop.
/// <see cref="DisposeAsync"/> stops the server.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> <see cref="StartAsync"/> and
/// <see cref="StopAsync"/> are non-virtual over <see cref="StartCoreAsync"/> and
/// <see cref="StopCoreAsync"/>; <see cref="Sessions"/> is the one abstract public member, state the
/// leaf owns, which a leaf may override covariantly. A leaf re-exposes its typed engine with a
/// <c>new</c> property over a typed field of its own. The leaves live in the model assemblies, so
/// the constructor is <c>protected</c>. Until phase 6 the base also implements
/// <see cref="IDatabaseServer"/>, and <see cref="Context"/> stays as a temporary abstract member,
/// because the hosting layer reads a server's engine through it until then.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseServer : IDatabaseServer
{
    private const int Created = 0;
    private const int Running = 1;
    private const int Stopped = 2;

    private readonly DatabaseEngine _engine;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _lifecycle = Created;

    /// <summary>
    /// Initializes a new, inert server over the engine it fronts.
    /// </summary>
    /// <param name="engine">The engine the server fronts; the composition root owns it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    protected DatabaseServer(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>
    /// Gets the engine this server fronts.
    /// </summary>
    public DatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets a point-in-time snapshot of the sessions currently active on this server.
    /// </summary>
    public abstract IReadOnlyCollection<DatabaseServerSession> Sessions { get; }

    /// <summary>
    /// Gets the composed state of the server through the phase-6 bridge: the engine it fronts and
    /// its active sessions. Phase 6 deletes it with <see cref="IDatabaseServerContext"/>; read
    /// <see cref="Engine"/> and <see cref="Sessions"/> instead.
    /// </summary>
    public abstract IDatabaseServerContext Context { get; }

    /// <summary>
    /// Gets whether the server is running: started, and not yet stopped.
    /// </summary>
    protected bool IsRunning => Volatile.Read(ref _lifecycle) == Running;

    /// <summary>
    /// Starts accepting connections. A start while the server runs returns at once; a start that
    /// fails leaves the server stopped for good.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the start.</param>
    /// <returns>A task that completes once the server is accepting.</returns>
    /// <exception cref="ObjectDisposedException">The server was stopped, or a start of it failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the start began.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int lifecycle = Volatile.Read(ref _lifecycle);
            ObjectDisposedException.ThrowIf(lifecycle == Stopped, this);
            if (lifecycle == Running)
            {
                return;
            }

            try
            {
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed start is terminal: the leaf released what the start acquired, and a
                // later stop has nothing left to do.
                Volatile.Write(ref _lifecycle, Stopped);
                throw;
            }

            Volatile.Write(ref _lifecycle, Running);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stops accepting connections and drains the active sessions gracefully, within the leaf's
    /// drain budget. Terminal and idempotent: a stopped server stays stopped, and a later stop
    /// returns at once.
    /// </summary>
    /// <param name="cancellationToken">Bounds the drain.</param>
    /// <returns>A task that completes once the drain has finished.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the stop began.</exception>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _lifecycle) == Stopped)
            {
                return;
            }

            Volatile.Write(ref _lifecycle, Stopped);
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stops the server (<see cref="StopAsync"/>).
    /// </summary>
    /// <returns>A task that completes once the server is stopped.</returns>
    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>
    /// Binds the leaf's listener and starts accepting. Called once, under the lifecycle gate, on an
    /// inert server. A start that throws leaves the server stopped, so the leaf releases what it
    /// acquired before it rethrows.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the start.</param>
    /// <returns>A task that completes once the server is accepting.</returns>
    protected abstract Task StartCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops accepting, drains the sessions and releases the listener. Called once, under the
    /// lifecycle gate, whether or not the server started (a server that never started releases its
    /// listener all the same); <see cref="IsRunning"/> is already false.
    /// </summary>
    /// <param name="cancellationToken">Bounds the drain.</param>
    /// <returns>A task that completes once the drain has finished.</returns>
    protected abstract Task StopCoreAsync(CancellationToken cancellationToken);
}
