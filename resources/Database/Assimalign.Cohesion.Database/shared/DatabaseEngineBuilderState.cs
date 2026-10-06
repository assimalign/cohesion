using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The build state every model's engine builder shares: the worker and server factories composed
/// for one engine, the freeze after the one build attempt, and the rollback of a build that fails.
/// </summary>
/// <typeparam name="TEngine">The model's engine, which every factory receives.</typeparam>
/// <typeparam name="TWorker">The worker type the model's factories return.</typeparam>
/// <typeparam name="TServer">The server type the model's factories return.</typeparam>
/// <remarks>
/// <para>
/// <b>Typed over the engine (concrete-types plan, step P4.0, #1260).</b> A factory receives the
/// model's own engine, so each model's sealed builder offers typed <c>AddWorker</c> and
/// <c>AddServer</c>. Every model's engine derives from <see cref="DatabaseEngine"/> since phase 4,
/// and every builder composes <see cref="DatabaseEngineWorker"/> and <see cref="DatabaseServer"/>;
/// the product types stay parameters, constrained to the root interfaces the bases implement,
/// until phase 6 deletes the interfaces, fixes the products to the bases and constrains
/// <typeparamref name="TEngine"/> to <see cref="DatabaseEngine"/>.
/// </para>
/// <para>
/// <b>The leaf attaches; the base checks.</b> A builder cannot call the protected attach members
/// of the engine it built, so <see cref="Complete(TEngine, Action{IEnumerable{TWorker}, IEnumerable{TServer}}, Func{TWorker, ValueTask})"/>
/// takes the leaf's internal compose method, which attaches each product through
/// <c>AttachWorker</c> and <c>AttachServer</c> and freezes the engine with
/// <c>CompleteComposition</c>. The base refuses a product attached twice, a server that fronts
/// another engine, a duplicate worker name and a worker that is not free (another engine owns it,
/// or it was released), so this state makes none of those checks: it runs the factories, refuses
/// a null product, and disposes whatever a failed build leaves unowned.
/// </para>
/// <para>
/// <b>The compose method's contract, checked here.</b> It reads the workers once, all the way
/// through, then the servers once, all the way through, and attaches each product before it
/// requests the next. The disposal on failure rests on that: only the product last handed out can
/// be unattached. A compose method that reads a sequence twice, requests a server before it
/// attached every worker, reads past a product it did not attach, or returns before it read both
/// sequences to the end fails the build with <see cref="InvalidOperationException"/>, with the
/// product it left unattached disposed and no later factory run, instead of leaking products or
/// dropping factories.
/// </para>
/// <para>
/// <b>A rejected product is released by its owner's rules.</b> A rejected server or engine is
/// disposed through its public disposal. A worker has none (concrete-types plan, row 7): the
/// leaf hands <see cref="Complete(TEngine, Action{IEnumerable{TWorker}, IEnumerable{TServer}}, Func{TWorker, ValueTask})"/>
/// its internal re-exposure of the engine base's protected
/// <see cref="DatabaseEngine.ReleaseUnownedWorkerAsync"/>, the same way it hands over its compose
/// method, and a rejected worker no engine owns runs its release hook through it. A worker the
/// engine already owns (a repeated product, a built-in worker a factory returned) is the engine's
/// to release, so the state leaves it alone; one another engine owns is left alone by the release
/// itself. The bridge overload that composed through an unadopted engine's own attach members was
/// deleted with the last model's phase-4 PR (Sql, #1260).
/// </para>
/// </remarks>
internal sealed class DatabaseEngineBuilderState<TEngine, TWorker, TServer>
    where TEngine : class, IDatabaseEngine
    where TWorker : class, IDatabaseEngineWorker
    where TServer : class, IDatabaseServer
{
    private readonly List<Func<TEngine, TWorker>> _workers = [];
    private readonly List<Func<TEngine, TServer>> _servers = [];
    private int _buildAttempted;
    private TEngine? _completedEngine;

    // The product the compose method was last handed and has not asked past: the one it was
    // attaching when it failed. The sequences clear it once they see it attached.
    private object? _pending;

    // How far the compose method has read the two sequences, which it reads once each, in order.
    private ComposeProgress _progress;

    /// <summary>
    /// Throws once a build was attempted: options and factories are frozen from then on.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    public void EnsureMutable()
    {
        if (Volatile.Read(ref _buildAttempted) != 0)
        {
            throw new InvalidOperationException("Engine composition is frozen after a build attempt.");
        }
    }

    /// <summary>
    /// Registers a worker factory, run against the engine when it is built.
    /// </summary>
    /// <param name="configure">Creates the worker for the built engine.</param>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public void AddWorker(Func<TEngine, TWorker> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _workers.Add(configure);
    }

    /// <summary>
    /// Registers a server factory, run against the engine when it is built, after every worker.
    /// </summary>
    /// <param name="configure">Creates the server for the built engine.</param>
    /// <exception cref="InvalidOperationException">A build was attempted.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public void AddServer(Func<TEngine, TServer> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _servers.Add(configure);
    }

    /// <summary>
    /// Starts the one build attempt the builder supports.
    /// </summary>
    /// <exception cref="InvalidOperationException">A build was already attempted.</exception>
    public void BeginBuild()
    {
        if (Interlocked.Exchange(ref _buildAttempted, 1) != 0)
        {
            throw new InvalidOperationException("An engine builder supports one build attempt.");
        }
    }

    /// <summary>
    /// Runs the registered factories against the built engine and attaches their products through
    /// the leaf's compose method. When anything fails, the product being attached is disposed unless
    /// the engine already owns it, then the engine is disposed with everything it owns, and the
    /// failure is rethrown unchanged: whatever a factory, the compose method or an attach threw.
    /// </summary>
    /// <param name="engine">The engine the builder created.</param>
    /// <param name="compose">
    /// The leaf's internal compose method: it enumerates the workers, then the servers, once each
    /// and to the end, attaching every product before it asks for the next, and then freezes the
    /// engine. A factory runs when its product is requested, so it observes the products attached
    /// before it, and every server factory runs after every worker is attached.
    /// </param>
    /// <param name="releaseWorker">
    /// The leaf's internal re-exposure of <see cref="DatabaseEngine.ReleaseUnownedWorkerAsync"/>:
    /// releases a rejected worker no engine owns, and does nothing on one an engine owns.
    /// </param>
    /// <returns><paramref name="engine"/>, composed.</returns>
    /// <exception cref="InvalidOperationException">
    /// A factory returned null; the engine refused a product (the base refuses a product attached
    /// twice, a server that fronts another engine, a duplicate worker name and a worker another
    /// engine owns or that was released); or <paramref name="compose"/> broke its contract (it read
    /// a sequence twice, requested a server before it attached every worker, read past a product it
    /// did not attach, or returned before it read both sequences to the end).
    /// </exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public TEngine Complete(TEngine engine, Action<IEnumerable<TWorker>, IEnumerable<TServer>> compose, Func<TWorker, ValueTask> releaseWorker)
    {
        try
        {
            try
            {
                compose(
                    Produce(engine, _workers, servers: false, "A worker factory returned null."),
                    Produce(engine, _servers, servers: true, "A server factory returned null."));

                // A sequence never read to the end dropped its remaining factories.
                if (_progress != ComposeProgress.ServersAttached)
                {
                    throw new InvalidOperationException("The compose method returned before it attached every product.");
                }
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // The ownership test guards the disposal, not the attach: the engine refuses a
                // product it already owns like any other, and that product is the engine's to dispose.
                var rejected = _pending;
                _pending = null;
                if (rejected is TWorker worker && !IsAttached(engine, worker))
                {
                    ReleaseRejected(worker, releaseWorker, failure);
                }
                else if (rejected is not null && !IsAttached(engine, rejected))
                {
                    DisposeRejected(rejected, failure);
                }

                throw;
            }

            _pending = null;
            _completedEngine = engine;
            return engine;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            DisposeRejected(engine, failure);
            throw;
        }
    }

    /// <summary>
    /// Disposes the engine a completed build returned, when the composition that consumed it
    /// failed afterwards.
    /// </summary>
    /// <param name="failure">The failure that abandoned the engine.</param>
    /// <exception cref="AggregateException"><paramref name="failure"/>, together with the engine's disposal failure.</exception>
    public void Abort(Exception failure)
    {
        var engine = _completedEngine;
        _completedEngine = null;
        if (engine is not null)
        {
            DisposeRejected(engine, failure);
        }
    }

    // One factory per requested product, in registration order. The checks run when the compose
    // method reads, so they enforce its contract: each sequence read once, the workers to the end
    // before the servers, and each product attached before the next is requested. A check that
    // fails throws while the product left unattached is still pending, so Complete disposes it,
    // and no later factory runs.
    private IEnumerable<TProduct> Produce<TProduct>(TEngine engine, List<Func<TEngine, TProduct>> factories, bool servers, string nullProduct)
        where TProduct : class
    {
        var (reading, required, attached) = servers
            ? (ComposeProgress.ReadingServers, ComposeProgress.WorkersAttached, ComposeProgress.ServersAttached)
            : (ComposeProgress.ReadingWorkers, ComposeProgress.None, ComposeProgress.WorkersAttached);
        if (_progress >= reading)
        {
            throw new InvalidOperationException("The compose method read a product sequence twice.");
        }

        if (_progress != required)
        {
            throw new InvalidOperationException("The compose method requested a server before it attached every worker.");
        }

        _progress = reading;
        foreach (var factory in factories)
        {
            ThrowIfPendingUnattached(engine);
            _pending = null;
            var product = factory(engine) ?? throw new InvalidOperationException(nullProduct);
            _pending = product;
            yield return product;
        }

        ThrowIfPendingUnattached(engine);
        _pending = null;
        _progress = attached;
    }

    private void ThrowIfPendingUnattached(TEngine engine)
    {
        if (_pending is { } previous && !IsAttached(engine, previous))
        {
            throw new InvalidOperationException("The compose method read past a product it did not attach.");
        }
    }

    private static bool IsAttached(TEngine engine, object product)
    {
        foreach (var worker in engine.Workers)
        {
            if (ReferenceEquals(worker, product))
            {
                return true;
            }
        }

        foreach (var server in engine.Servers)
        {
            if (ReferenceEquals(server, product))
            {
                return true;
            }
        }

        return false;
    }

    // A worker has no public disposal (row 7): the leaf's release runs its hook once, unless an
    // engine owns it.
    private static void ReleaseRejected(TWorker worker, Func<TWorker, ValueTask> releaseWorker, Exception failure)
    {
        try
        {
            Task.Run(async () => await releaseWorker(worker).ConfigureAwait(false))
                .GetAwaiter().GetResult();
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            throw new AggregateException(failure, cleanup);
        }
    }

    private static void DisposeRejected(object product, Exception failure)
    {
        try
        {
            if (product is IAsyncDisposable asyncDisposable)
            {
                Task.Run(async () => await asyncDisposable.DisposeAsync().ConfigureAwait(false))
                    .GetAwaiter().GetResult();
            }
            else if (product is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            throw new AggregateException(failure, cleanup);
        }
    }

    // The compose method's reads, in the one order its contract allows.
    private enum ComposeProgress
    {
        None,
        ReadingWorkers,
        WorkersAttached,
        ReadingServers,
        ServersAttached,
    }
}
