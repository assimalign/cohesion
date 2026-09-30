using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection.Properties;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class ServiceProviderEngineScope : IServiceScope, IServiceProvider, IServiceScopeFactory, IAsyncDisposable
{
    // Up to this many captured disposables, BeginDispose removes duplicates with a reference scan
    // instead of allocating a set.
    private const int maxDisposablesForLinearDeduplication = 16;

    // For testing only. An entry is null where BeginDispose removed a repeated capture of one instance.
    internal IList<object?> Disposables => _disposables ?? (IList<object?>)Array.Empty<object?>();

    private bool _disposed;
    private List<object?>? _disposables;

    public ServiceProviderEngineScope(ServiceProvider provider, bool isRootScope)
    {
        ResolvedServices = new();
        RootProvider = provider;
        IsRootScope = isRootScope;
    }

    internal Dictionary<CallSiteServiceCacheKey, object?> ResolvedServices { get; }

    // This lock protects state on the scope, in particular, for the root scope, it protects
    // the list of disposable entries only, since ResolvedServices are cached on CallSites
    // For other scopes, it protects ResolvedServices and the list of disposables
    internal object Sync => ResolvedServices;

    public bool IsRootScope { get; }

    internal ServiceProvider RootProvider { get; }

    public object GetService(Type serviceType)
    {
        if (_disposed)
        {
            ThrowHelper.ThrowObjectDisposedException();
        }

        return RootProvider.GetService(serviceType, this);
    }

    public IServiceProvider ServiceProvider => this;
    public IServiceScope CreateScope() => RootProvider.CreateScope();
    internal object CaptureDisposable(object service)
    {
        if (ReferenceEquals(this, service) || !(service is IDisposable || service is IAsyncDisposable))
        {
            return service;
        }
        var disposed = false;
        lock (Sync)
        {
            if (_disposed)
            {
                disposed = true;
            }
            else
            {
                _disposables ??= new List<object?>();
                _disposables.Add(service);
            }
        }
        // Don't run customer code under the lock
        if (disposed)
        {
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                // sync over async, for the rare case that an object only implements IAsyncDisposable and may end up starving the thread pool.
                // The lambda captures this local, not the parameter: a captured parameter is copied into
                // a closure on entry to the method, which cost every call an allocation.
                object asyncDisposable = service;
                Task.Run(() => ((IAsyncDisposable)asyncDisposable).DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }

            ThrowHelper.ThrowObjectDisposedException();
        }

        return service;
    }
    public void Dispose()
    {
        List<object?>? toDispose = BeginDispose();
        if (toDispose is null)
        {
            return;
        }

        object? exceptions = null;
        for (int i = toDispose.Count - 1; i >= 0; i--)
        {
            object? entry = toDispose[i];
            if (entry is null)
            {
                continue;
            }

            try
            {
                if (entry is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else
                {
                    throw new InvalidOperationException(
                        Resources.GetAsyncDisposableServiceDisposeExceptionMessage(
                            TypeNameHelper.GetTypeDisplayName(entry)));
                }
            }
            catch (Exception exception)
            {
                // Any failure is caught so the remaining services are still disposed; all of them are
                // rethrown once disposal is complete.
                AddException(ref exceptions, exception);
            }
        }

        ThrowExceptions(exceptions);
    }
    public ValueTask DisposeAsync()
    {
        List<object?>? toDispose = BeginDispose();
        if (toDispose is null)
        {
            return default;
        }

        object? exceptions = null;
        for (int i = toDispose.Count - 1; i >= 0; i--)
        {
            object? entry = toDispose[i];
            if (entry is null)
            {
                continue;
            }

            try
            {
                if (entry is IAsyncDisposable asyncDisposable)
                {
                    ValueTask vt = asyncDisposable.DisposeAsync();
                    if (!vt.IsCompletedSuccessfully)
                    {
                        return Await(i, vt, toDispose, exceptions);
                    }

                    // If its a IValueTaskSource backed ValueTask,
                    // inform it its result has been read so it can reset
                    vt.GetAwaiter().GetResult();
                }
                else
                {
                    ((IDisposable)entry).Dispose();
                }
            }
            catch (Exception exception)
            {
                // Any failure is caught so the remaining services are still disposed; all of them are
                // reported once disposal is complete.
                AddException(ref exceptions, exception);
            }
        }

        return exceptions is null
            ? default
            : new ValueTask(Task.FromException(ToException(exceptions)));

        static async ValueTask Await(int i, ValueTask vt, List<object?> toDispose, object? exceptions)
        {
            try
            {
                await vt.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Caught so the remaining services are still disposed; reported below.
                AddException(ref exceptions, exception);
            }

            // vt is acting on the disposable at index i,
            // decrement it and move to the next iteration
            i--;

            for (; i >= 0; i--)
            {
                object? entry = toDispose[i];
                if (entry is null)
                {
                    continue;
                }

                try
                {
                    if (entry is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        ((IDisposable)entry).Dispose();
                    }
                }
                catch (Exception exception)
                {
                    // Caught so the remaining services are still disposed; reported below.
                    AddException(ref exceptions, exception);
                }
            }

            ThrowExceptions(exceptions);
        }
    }

    // One failure is held as an ExceptionDispatchInfo, so it is rethrown as itself with its stack
    // trace intact; the list, and the AggregateException, are allocated only when a second one arrives.
    private static void AddException(ref object? exceptions, Exception exception)
    {
        if (exceptions is null)
        {
            exceptions = ExceptionDispatchInfo.Capture(exception);
        }
        else if (exceptions is ExceptionDispatchInfo first)
        {
            exceptions = new List<Exception> { first.SourceException, exception };
        }
        else
        {
            ((List<Exception>)exceptions).Add(exception);
        }
    }

    private static void ThrowExceptions(object? exceptions)
    {
        if (exceptions is ExceptionDispatchInfo single)
        {
            single.Throw();
        }
        if (exceptions is List<Exception> several)
        {
            throw new AggregateException(several);
        }
    }

    private static Exception ToException(object exceptions) =>
        exceptions is ExceptionDispatchInfo single
            ? single.SourceException
            : new AggregateException((List<Exception>)exceptions);

    private List<object?>? BeginDispose()
    {
        lock (Sync)
        {
            if (_disposed)
            {
                return null;
            }

            // Track statistics about the scope (number of disposable objects and number of disposed services)
            ServiceEventSource.Log.ScopeDisposed(RootProvider.GetHashCode(), ResolvedServices.Count, _disposables?.Count ?? 0);

            // We've transitioned to the disposed state, so future calls to
            // CaptureDisposable will immediately dispose the object.
            // No further changes to _disposables are allowed.
            _disposed = true;

            // ResolvedServices is never cleared for singletons because there might be a compilation running in background
            // trying to get a cached singleton service. If it doesn't find it
            // it will try to create a new one which will result in an ObjectDisposedException.
        }
        if (IsRootScope && !RootProvider.IsDisposed)
        {
            // If this ServiceProviderEngineScope instance is a root scope, disposing this instance will need to dispose the RootProvider too.
            // Otherwise the RootProvider will never get disposed and will leak.
            // Note, if the RootProvider get disposed first, it will automatically dispose all attached ServiceProviderEngineScope objects.
            RootProvider.Dispose();
        }

        // _disposables no longer changes once _disposed is set.
        if (_disposables is not { Count: > 0 } disposables)
        {
            return null;
        }

        RemoveRepeatedCaptures(disposables);
        return disposables;
    }

    // One instance can be captured more than once, for example a singleton exposed as several services
    // through forwarding factories. Every capture after the first is nulled in place, so the instance
    // is disposed once, from the position of its first capture: after the services captured later,
    // which include everything that depends on it.
    private static void RemoveRepeatedCaptures(List<object?> disposables)
    {
        int count = disposables.Count;
        if (count > maxDisposablesForLinearDeduplication)
        {
            var seen = new HashSet<object>(count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < count; i++)
            {
                if (disposables[i] is object entry && !seen.Add(entry))
                {
                    disposables[i] = null;
                }
            }
            return;
        }

        for (int i = 0; i < count; i++)
        {
            object? entry = disposables[i];
            if (entry is null)
            {
                continue;
            }

            for (int j = i + 1; j < count; j++)
            {
                if (ReferenceEquals(entry, disposables[j]))
                {
                    disposables[j] = null;
                }
            }
        }
    }
}
