using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;


/// <summary>
/// State for an expired-but-not-yet-disposed inner handler. Disposed by
/// <see cref="HttpClientFactory.CleanupExpired"/> once every wrapping
/// <see cref="LifetimeTrackingHttpMessageHandler"/> has been GC'd &#8211; which means
/// every <see cref="HttpClient"/> built on this handler has been collected.
/// </summary>
internal sealed class ExpiredHandlerEntry
{
    private readonly IReadOnlyList<WeakReference<LifetimeTrackingHttpMessageHandler>> _wrapperRefs;

    public ExpiredHandlerEntry(
        HttpMessageHandler innerHandler,
        IReadOnlyList<WeakReference<LifetimeTrackingHttpMessageHandler>> wrapperRefs)
    {
        InnerHandler = innerHandler;
        _wrapperRefs = wrapperRefs;
    }

    public HttpMessageHandler InnerHandler { get; }

    /// <summary>
    /// Returns <see langword="true"/> when at least one wrapping handler is still
    /// reachable. Used by the cleanup pass to decide whether the inner handler is safe
    /// to dispose.
    /// </summary>
    public bool HasLiveWrappers()
    {
        foreach (WeakReference<LifetimeTrackingHttpMessageHandler> reference in _wrapperRefs)
        {
            if (reference.TryGetTarget(out _))
            {
                return true;
            }
        }
        return false;
    }
}
