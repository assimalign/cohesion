using Assimalign.Cohesion.DependencyInjection.Properties;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class ServiceProviderEngineScope : IServiceScope, IServiceProvider, IServiceScopeFactory, IAsyncDisposable
{
    // For testing only
    internal IList<object> Disposables => this.disposables ?? (IList<object>)Array.Empty<object>();

    private bool disposed;
    private List<object> disposables;

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
        if (disposed)
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
            if (this.disposed)
            {
                disposed = true;
            }
            else
            {
                this.disposables ??= new List<object>();
                this.disposables.Add(service);
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
                Task.Run(() => ((IAsyncDisposable)service).DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }

            ThrowHelper.ThrowObjectDisposedException();
        }

        return service;
    }
    public void Dispose()
    {
        List<object> toDispose = BeginDispose();

        if (toDispose != null)
        {
            for (int i = toDispose.Count - 1; i >= 0; i--)
            {
                if (toDispose[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else
                {
                    throw new InvalidOperationException(
                        Resources.GetAsyncDisposableServiceDisposeExceptionMessage(
                            TypeNameHelper.GetTypeDisplayName(toDispose[i])));
                }
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        List<object> toDispose = BeginDispose();

        if (toDispose != null)
        {
            try
            {
                for (int i = toDispose.Count - 1; i >= 0; i--)
                {
                    object disposable = toDispose[i];
                    if (disposable is IAsyncDisposable asyncDisposable)
                    {
                        ValueTask vt = asyncDisposable.DisposeAsync();
                        if (!vt.IsCompletedSuccessfully)
                        {
                            return Await(i, vt, toDispose);
                        }

                        // If its a IValueTaskSource backed ValueTask,
                        // inform it its result has been read so it can reset
                        vt.GetAwaiter().GetResult();
                    }
                    else
                    {
                        ((IDisposable)disposable).Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return new ValueTask(Task.FromException(ex));
            }
        }

        return default;

        static async ValueTask Await(int i, ValueTask vt, List<object> toDispose)
        {
            await vt.ConfigureAwait(false);
            // vt is acting on the disposable at index i,
            // decrement it and move to the next iteration
            i--;

            for (; i >= 0; i--)
            {
                object disposable = toDispose[i];
                if (disposable is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    ((IDisposable)disposable).Dispose();
                }
            }
        }
    }
    private List<object> BeginDispose()
    {
        lock (Sync)
        {
            if (disposed)
            {
                return null;
            }

            // Track statistics about the scope (number of disposable objects and number of disposed services)
            ServiceEventSource.Log.ScopeDisposed(RootProvider.GetHashCode(), ResolvedServices.Count, disposables?.Count ?? 0);

            // We've transitioned to the disposed state, so future calls to
            // CaptureDisposable will immediately dispose the object.
            // No further changes to _state.Disposables, are allowed.
            disposed = true;

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
        return disposables;
    }
}
