using System.Net.Http;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A delegating message handler that wraps the factory's pooled inner handler. The
/// wrapper exists so each <see cref="HttpClient"/> the factory hands out has its own
/// disposable handle &#8211; disposing the client disposes this wrapper but leaves the
/// inner handler alive for other clients sharing it.
/// </summary>
/// <remarks>
/// <para>
/// The factory rotates the inner handler on a schedule. When the rotation moves the inner
/// handler to the expired list, the factory keeps a <see cref="System.WeakReference"/> to
/// every <see cref="LifetimeTrackingHttpMessageHandler"/> it produced from that inner
/// handler. A periodic cleanup pass disposes the inner handler once GC reclaims the last
/// surviving wrapper &#8211; meaning every <see cref="HttpClient"/> built on it has been
/// disposed and collected.
/// </para>
/// <para>
/// This is the same shape Microsoft's reference <c>IHttpClientFactory</c> uses
/// (<see href="https://learn.microsoft.com/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests"/>);
/// it pairs cheap client disposal with safe handler lifetime.
/// </para>
/// </remarks>
internal sealed class LifetimeTrackingHttpMessageHandler : DelegatingHandler
{
    public LifetimeTrackingHttpMessageHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>
    /// Suppresses base disposal. The inner handler is owned by
    /// <see cref="HttpClientFactory"/> and disposed by it when the rotation pool is ready
    /// to release it.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        // Intentionally do NOT call base.Dispose(disposing). The base implementation would
        // dispose InnerHandler, which we explicitly do not own here.
    }
}
