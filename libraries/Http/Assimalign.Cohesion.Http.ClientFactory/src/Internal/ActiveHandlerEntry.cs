using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// State for the currently-active rotation slot of a named client. Holds the shared inner
/// handler, its expiration timestamp, and weak references to the per-client wrapping
/// handlers so the cleanup pass can decide when the inner handler is safe to dispose.
/// </summary>
internal sealed class ActiveHandlerEntry
{
    private readonly object _wrapperLock = new();
    private readonly List<WeakReference<LifetimeTrackingHttpMessageHandler>> _wrappers = new();

    public ActiveHandlerEntry(HttpMessageHandler handler, DateTimeOffset expiresAtUtc)
    {
        Handler = handler;
        ExpiresAtUtc = expiresAtUtc;
    }

    public HttpMessageHandler Handler { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public void RegisterWrapper(LifetimeTrackingHttpMessageHandler wrapper)
    {
        lock (_wrapperLock)
        {
            _wrappers.Add(new WeakReference<LifetimeTrackingHttpMessageHandler>(wrapper));
        }
    }

    public IReadOnlyList<WeakReference<LifetimeTrackingHttpMessageHandler>> GetWrapperRefs()
    {
        lock (_wrapperLock)
        {
            return _wrappers.ToArray();
        }
    }
}
