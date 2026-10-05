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
/// model's own engine, so a model's sealed builder can offer typed <c>AddWorker</c> and
/// <c>AddServer</c> once its engine derives from <see cref="DatabaseEngine"/>. The product types
/// are parameters only for the bridge: a model that has not adopted the root bases composes the
/// root interfaces (<see cref="IDatabaseEngineWorker"/>, <see cref="IDatabaseServer"/>), and one
/// that has composes <see cref="DatabaseEngineWorker"/> and <see cref="DatabaseServer"/>, which
/// implement those interfaces until phase 6. Phase 6 deletes the interfaces, fixes the products to
/// the bases and constrains <typeparamref name="TEngine"/> to <see cref="DatabaseEngine"/>.
/// </para>
/// <para>
/// <b>The leaf attaches; the base checks.</b> A builder cannot call the protected attach members
/// of the engine it built, so <see cref="Complete(TEngine, Action{IEnumerable{TWorker}, IEnumerable{TServer}})"/>
/// takes the leaf's internal compose method, which attaches each product through
/// <c>AttachWorker</c> and <c>AttachServer</c> and freezes the engine with
/// <c>CompleteComposition</c>. The base refuses a product attached twice, a server that fronts
/// another engine and a duplicate worker name, so this state makes none of those checks: it runs
/// the factories, refuses a null product, and disposes whatever a failed build leaves unowned.
/// Until a model's engine derives from the base, its builder composes through
/// <see cref="Complete(TEngine, Action{TWorker}, Action{TServer})"/>, which makes the base's
/// product checks (a product attached twice, a server that fronts another engine) over the
/// engine's own attach members; the worker-name check stays the engine's, as before.
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
    // attaching when it failed. The sequences clear it when the next product is requested.
    private object? _pending;

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
    /// failure is rethrown.
    /// </summary>
    /// <param name="engine">The engine the builder created.</param>
    /// <param name="compose">
    /// The leaf's internal compose method: it enumerates the workers, then the servers, once each,
    /// attaching every product before it asks for the next, and then freezes the engine. A factory
    /// runs when its product is requested, so it observes the products attached before it.
    /// </param>
    /// <returns><paramref name="engine"/>, composed.</returns>
    /// <exception cref="InvalidOperationException">A factory returned null, or the engine refused a product.</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public TEngine Complete(TEngine engine, Action<IEnumerable<TWorker>, IEnumerable<TServer>> compose)
    {
        try
        {
            try
            {
                compose(
                    Produce(engine, _workers, "A worker factory returned null."),
                    Produce(engine, _servers, "A server factory returned null."));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // The ownership test guards the disposal, not the attach: the engine refuses a
                // product it already owns like any other, and that product is the engine's to dispose.
                var rejected = _pending;
                _pending = null;
                if (rejected is not null && !IsAttached(engine, rejected))
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
    /// The bridge for a model whose engine does not derive from <see cref="DatabaseEngine"/> yet:
    /// composes through the engine's own attach members, making the checks the base's attach makes
    /// (a product attached twice, a server that fronts another engine). Each model's phase-4 PR moves
    /// its builder to the compose-method overload, and the last one deletes this bridge.
    /// </summary>
    /// <param name="engine">The engine the builder created.</param>
    /// <param name="attachWorker">The engine's internal worker attach.</param>
    /// <param name="attachServer">The engine's internal server attach.</param>
    /// <returns><paramref name="engine"/>, composed.</returns>
    /// <exception cref="InvalidOperationException">A factory returned null, or a product was refused.</exception>
    /// <exception cref="AggregateException">The failure, together with a failure to dispose what it rejected.</exception>
    public TEngine Complete(TEngine engine, Action<TWorker> attachWorker, Action<TServer> attachServer)
        => Complete(engine, (workers, servers) =>
        {
            foreach (var worker in workers)
            {
                ThrowIfAttached(engine, worker);
                attachWorker(worker);
            }

            foreach (var server in servers)
            {
                ThrowIfAttached(engine, server);
                if (!ReferenceEquals(server.Context.Engine, engine))
                {
                    throw new InvalidOperationException("A nested server must front its owning engine.");
                }

                attachServer(server);
            }
        });

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

    // One factory per requested product, in registration order. Requesting the next product means
    // the compose method attached the previous one, so the pending product is cleared first.
    private IEnumerable<TProduct> Produce<TProduct>(TEngine engine, List<Func<TEngine, TProduct>> factories, string nullProduct)
        where TProduct : class
    {
        foreach (var factory in factories)
        {
            _pending = null;
            var product = factory(engine) ?? throw new InvalidOperationException(nullProduct);
            _pending = product;
            yield return product;
        }

        _pending = null;
    }

    private static void ThrowIfAttached(TEngine engine, object product)
    {
        if (IsAttached(engine, product))
        {
            throw new InvalidOperationException("A composition product cannot be registered twice.");
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
}
