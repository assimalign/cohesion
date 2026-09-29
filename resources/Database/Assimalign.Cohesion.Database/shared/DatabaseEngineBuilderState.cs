using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

internal sealed class DatabaseEngineBuilderState
{
    private readonly List<Func<IDatabaseEngine, IDatabaseEngineWorker>> _workers = [];
    private readonly List<Func<IDatabaseEngine, IDatabaseServer>> _servers = [];
    private int _buildAttempted;
    private IDatabaseEngine? _completedEngine;

    public void EnsureMutable()
    {
        if (Volatile.Read(ref _buildAttempted) != 0)
        {
            throw new InvalidOperationException("Engine composition is frozen after a build attempt.");
        }
    }

    public void AddWorker(Func<IDatabaseEngine, IDatabaseEngineWorker> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _workers.Add(configure);
    }

    public void AddServer(Func<IDatabaseEngine, IDatabaseServer> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _servers.Add(configure);
    }

    public void BeginBuild()
    {
        if (Interlocked.Exchange(ref _buildAttempted, 1) != 0)
        {
            throw new InvalidOperationException("An engine builder supports one build attempt.");
        }
    }

    public IDatabaseEngine Complete(
        IDatabaseEngine engine,
        Action<IDatabaseEngineWorker> attachWorker,
        Action<IDatabaseServer> attachServer)
    {
        try
        {
            foreach (var factory in _workers)
            {
                var worker = factory(engine)
                    ?? throw new InvalidOperationException("A worker factory returned null.");
                if (IsOwnedProduct(engine, worker))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }
                try
                {
                    attachWorker(worker);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    DisposeRejected(worker, failure);
                    throw;
                }
            }

            foreach (var factory in _servers)
            {
                var server = factory(engine)
                    ?? throw new InvalidOperationException("A server factory returned null.");
                if (IsOwnedProduct(engine, server))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }
                try
                {
                    if (!ReferenceEquals(server.Context.Engine, engine))
                    {
                        throw new InvalidOperationException("A nested server must front its owning engine.");
                    }
                    attachServer(server);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    DisposeRejected(server, failure);
                    throw;
                }
            }
            _completedEngine = engine;
            return engine;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            DisposeRejected(engine, failure);
            throw;
        }
    }

    public void Abort(Exception failure)
    {
        var engine = _completedEngine;
        _completedEngine = null;
        if (engine is not null)
        {
            DisposeRejected(engine, failure);
        }
    }

    private static bool IsOwnedProduct(IDatabaseEngine engine, object product)
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
        return ReferenceEquals(engine, product);
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
