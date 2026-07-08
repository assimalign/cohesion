using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal abstract class TransportHttpContext : HttpContext
{
    // Backs RequestAborted. Linked to the transport-supplied token so the
    // exchange is aborted when the connection/stream is torn down, and can also
    // be tripped locally by Cancel().
    private readonly CancellationTokenSource _abortedSource;

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
        _abortedSource = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        // The parser pre-populates the feature collection when request-parse interceptors
        // attached features during the read (see IHttpRequestInterceptor); it is used directly —
        // no defaults-wrapper layer, which would add a second dictionary probe to every Get on
        // the hot path. A null/foreign collection degrades gracefully: null gets a fresh empty
        // collection (the zero-interceptor fast path), and a non-HttpFeatureCollection
        // implementation is wrapped as a read-through defaults source. On disposal the effective
        // collection is walked and every feature implementing IDisposable / IAsyncDisposable is
        // disposed.
        Features = features switch
        {
            null => new HttpFeatureCollection(),
            HttpFeatureCollection concrete => concrete,
            _ => new HttpFeatureCollection(features),
        };
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);

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
    public override CancellationToken RequestCancelled => _abortedSource.Token;

    /// <summary>
    /// The transport's raw response body sink for this exchange, or
    /// <see langword="null"/> when no response interceptors are registered (the buffered
    /// fast path). When a response feature has written to it
    /// (<see cref="HttpResponseBodyStream.HasStarted"/> is <see langword="true"/>) the
    /// transport's buffered <c>SendAsync</c> finalizes the already-started response
    /// instead of writing the buffered body again.
    /// </summary>
    internal HttpResponseBodyStream? ResponseBodySink { get; private set; }

    /// <summary>
    /// Runs the registered response interceptors, exposing the transport's raw response body
    /// <paramref name="sink"/> so a feature package can wrap it and install a typed response
    /// feature on <see cref="Features"/> — without the transport depending on that package.
    /// The sink is retained so the transport's send path can finalize it if the exchange streamed.
    /// </summary>
    /// <param name="interceptors">The snapshotted response interceptors, in registration order.</param>
    /// <param name="sink">The protocol-specific raw response body sink.</param>
    /// <param name="connectionTakeover">
    /// The protocol-specific connection-takeover capability, or <see langword="null"/> when the
    /// exchange cannot surrender its connection. Only the HTTP/1.1 transport supplies one — an
    /// HTTP/1.1 exchange owns its whole connection, whereas HTTP/2 / HTTP/3 exchanges are
    /// multiplexed streams over a shared connection.
    /// </param>
    /// <param name="interimResponseWriter">
    /// The protocol-specific interim-response capability (100 Continue / 103 Early Hints), or
    /// <see langword="null"/> when the transport does not offer it. Unlike
    /// <paramref name="connectionTakeover"/>, every version supplies one — an interim response is a
    /// separate status line / HEADERS block / field section written before the final response.
    /// </param>
    internal void RunResponseInterceptors(
        IHttpResponseInterceptor[] interceptors,
        HttpResponseBodyStream sink,
        IHttpConnectionTakeover? connectionTakeover = null,
        IHttpInterimResponseWriter? interimResponseWriter = null)
    {
        ResponseBodySink = sink;

        HttpResponseInterceptorContext interceptorContext = new()
        {
            Version = Version,
            Headers = Response.Headers,
            Features = Features,
            ConnectionInfo = ConnectionInfo,
            ResponseBody = sink,
            ConnectionTakeover = connectionTakeover,
            InterimResponseWriter = interimResponseWriter,
        };

        foreach (IHttpResponseInterceptor interceptor in interceptors)
        {
            interceptor.OnResponse(interceptorContext);
        }
    }

    /// <summary>
    /// Whether the application requested cancellation of this exchange via
    /// <see cref="Cancel"/>. Each transport's response path observes this and
    /// resets the single exchange (HTTP/2 <c>RST_STREAM</c>, HTTP/3 stream
    /// reset) instead of writing a response, without tearing down the connection.
    /// </summary>
    public bool CancelRequested { get; private set; }

    /// <summary>
    /// Cancels this exchange: records the request and trips
    /// <see cref="RequestCancelled"/> so in-flight handler work observes the
    /// cancellation. The actual wire reset is performed by the transport's
    /// response path on the next send for this exchange.
    /// </summary>
    public override void Cancel()
    {
        CancelRequested = true;

        try
        {
            _abortedSource.Cancel();
        }
        catch (System.ObjectDisposedException)
        {
            // The exchange already completed/disposed; cancellation is moot.
        }
    }


    public override async Task CancelAsync()
    {
        CancelRequested = true;

        try
        {
            await _abortedSource.CancelAsync();
        }
        catch (System.ObjectDisposedException)
        {
            // The exchange already completed/disposed; cancellation is moot.
        }
    }

    /// <summary>
    /// Disposes the request, releasing its features (any
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> feature
    /// in <see cref="Features"/> is disposed), then disposing the request
    /// and response body streams. The contract is request-scoped: a
    /// feature whose state needs deterministic cleanup at request end
    /// implements one of the disposal interfaces and is attached either at
    /// parse time by a registered <see cref="IHttpRequestInterceptor"/>
    /// (via <see cref="HttpConnectionListenerOptions.RequestInterceptors"/>) or
    /// later by middleware.
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
        _abortedSource.Dispose();
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
