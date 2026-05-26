using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal abstract class TransportHttpContext : HttpContext
{
    protected TransportHttpContext(
        HttpVersion version,
        TransportHttpRequest request,
        TransportHttpResponse response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IHttpFeatureCollection? features = null)
    {
        Version = version;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo;
        // If the listener's HttpConnectionListenerOptions.CreateFeatures
        // factory returned a collection, wrap it as the defaults source for
        // the per-request collection so features the user supplied are
        // visible via Get/iteration, but middleware Set calls land on the
        // local layer (they do not mutate the factory's collection). On
        // disposal the effective collection (local + defaults) is walked
        // and every feature implementing IDisposable / IAsyncDisposable is
        // disposed, regardless of which layer it lives on.
        Features = features is not null
            ? new HttpFeatureCollection(features)
            : new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        RequestAborted = requestAborted;

        // Wire the back-references last so the request and response can resolve
        // their owning context from this point forward. Construction order in
        // the transports is request -> response -> context, so the
        // HttpContext back-reference can only be installed after the context
        // itself exists.
        request.AttachContext(this);
        response.AttachContext(this);
    }

    public override HttpVersion Version { get; }

    public override HttpRequest Request { get; }

    public override HttpResponse Response { get; }

    public override HttpConnectionInfo ConnectionInfo { get; }

    public override HttpFeatureCollection Features { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    /// <summary>
    /// Disposes the request, releasing its features (any
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> feature
    /// in <see cref="Features"/> is disposed), then disposing the request
    /// and response body streams. The contract is request-scoped: a
    /// feature whose state needs deterministic cleanup at request end
    /// implements one of the disposal interfaces and is wired up at
    /// construction time via the
    /// <see cref="HttpConnectionListenerOptions.CreateFeatures"/> factory
    /// (or attached by middleware).
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // Snapshot the enumeration before disposing so a feature's
        // DisposeAsync that mutates the collection cannot break iteration.
        IHttpFeature[] snapshot = ToArray(Features);

        foreach (IHttpFeature feature in snapshot)
        {
            try
            {
                if (feature is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (feature is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Feature disposal is best-effort: one feature throwing
                // must not prevent the rest of the request from being
                // torn down, otherwise the response body / request body
                // stream below would leak.
            }
        }

        Request.Body.Dispose();
        Response.Body.Dispose();
    }

    private static IHttpFeature[] ToArray(IEnumerable<IHttpFeature> features)
    {
        // Enumerate once to size the array, then copy. Avoids an
        // allocation-heavy ToList/ToArray when the collection is empty,
        // which is the common case for requests that did not configure a
        // feature factory.
        int count = 0;
        foreach (IHttpFeature _ in features)
        {
            count++;
        }

        if (count == 0)
        {
            return Array.Empty<IHttpFeature>();
        }

        IHttpFeature[] result = new IHttpFeature[count];
        int index = 0;
        foreach (IHttpFeature feature in features)
        {
            result[index++] = feature;
        }
        return result;
    }
}
