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

    // The response-lifecycle interception state, retained for the exchange's lifetime when at
    // least one response interceptor is registered so the later lifecycle hooks
    // (BeforeResponseHeadAsync / AfterResponseAsync) re-invoke against the same context. Null on
    // the zero-interceptor fast path, which keeps the later invokers true no-ops.
    private IHttpResponseInterceptor[]? _responseInterceptors;
    private HttpResponseInterceptorContext? _responseInterception;
    private bool _beforeResponseHeadInvoked;
    private bool _afterResponseInvoked;

    // Set when the buffered send path commits the final response head (the streaming path is
    // tracked by the sink's own HasStarted). Feeds HasFinalResponseStarted so the exchange
    // control's probes (CanWriteInterimResponse / CanTakeOver) observe the buffered commit too —
    // without this, an AfterResponse hook could write a 1xx after the final response.
    private bool _finalResponseStarted;

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
    /// Whether the final response head has been (or is being) committed to the wire — by the
    /// buffered send path (<see cref="MarkFinalResponseStarted"/>) or by the streaming sink's
    /// first head commit. Once <see langword="true"/>, interim responses can no longer precede
    /// the final response and the exchange can no longer be taken over.
    /// </summary>
    internal bool HasFinalResponseStarted =>
        _finalResponseStarted || (ResponseBodySink?.HasStarted ?? false);

    /// <summary>
    /// Marks the final response head as committed. Called by each transport's buffered send path
    /// immediately before it encodes and writes the head (after the <c>BeforeResponseHead</c>
    /// hooks and directive re-checks have passed).
    /// </summary>
    internal void MarkFinalResponseStarted() => _finalResponseStarted = true;

    /// <summary>
    /// The exchange's current control-flow directive, derived from the transport flags the
    /// <see cref="IHttpExchangeControl"/> transitions drive: <see cref="Cancel"/> /
    /// <see cref="IHttpExchangeControl.Abort"/> maps to <see cref="HttpExchangeDirective.Abort"/>;
    /// a protocol that supports handing off its connection overrides this to report
    /// <see cref="HttpExchangeDirective.TakeOver"/> (see <c>Http1Context</c>).
    /// </summary>
    internal virtual HttpExchangeDirective ExchangeDirective =>
        CancelRequested ? HttpExchangeDirective.Abort : HttpExchangeDirective.Continue;

    /// <summary>
    /// Runs the registered response interceptors' <see cref="IHttpResponseInterceptor.BeforeResponse"/>
    /// hooks, exposing the transport's raw response body <paramref name="sink"/> and per-exchange
    /// <paramref name="control"/> so feature packages can wrap them and install typed response
    /// features on <see cref="Features"/> — without the transport depending on any feature package.
    /// The interceptors and context are retained so the exchange's later lifecycle hooks
    /// (<see cref="InvokeBeforeResponseHeadAsync"/> / <see cref="InvokeAfterResponseAsync"/>)
    /// re-invoke against the same context.
    /// </summary>
    /// <param name="interceptors">The snapshotted response interceptors, in registration order.</param>
    /// <param name="sink">The protocol-specific raw response body sink.</param>
    /// <param name="control">
    /// The protocol-specific exchange control (interim writes, takeover where physically possible,
    /// abort, the control-flow directive). Every version supplies one; capabilities a version
    /// cannot offer report unsupported through the control's probes.
    /// </param>
    internal void RunResponseInterceptors(
        IHttpResponseInterceptor[] interceptors,
        HttpResponseBodyStream sink,
        IHttpExchangeControl control)
    {
        ResponseBodySink = sink;
        _responseInterceptors = interceptors;
        _responseInterception = new HttpResponseInterceptorContext
        {
            Version = Version,
            Headers = Response.Headers,
            Features = Features,
            ConnectionInfo = ConnectionInfo,
            ResponseBody = sink,
            Control = control,
        };

        foreach (IHttpResponseInterceptor interceptor in interceptors)
        {
            interceptor.BeforeResponse(_responseInterception);
        }
    }

    /// <summary>
    /// Invokes the registered response interceptors'
    /// <see cref="IHttpResponseInterceptor.BeforeResponseHeadAsync"/> hooks exactly once per
    /// exchange, immediately before the final response head is committed — called by the buffered
    /// send path and by the streaming sink's first head commit, whichever happens first. A no-op
    /// on the zero-interceptor fast path. The guard flag is set before the hooks run so a hook
    /// that itself triggers a head commit cannot re-enter.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the hooks' work.</param>
    internal async ValueTask InvokeBeforeResponseHeadAsync(CancellationToken cancellationToken)
    {
        if (_responseInterceptors is null || _responseInterception is null || _beforeResponseHeadInvoked)
        {
            return;
        }

        _beforeResponseHeadInvoked = true;

        foreach (IHttpResponseInterceptor interceptor in _responseInterceptors)
        {
            await interceptor.BeforeResponseHeadAsync(_responseInterception, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes the registered response interceptors'
    /// <see cref="IHttpResponseInterceptor.AfterResponseAsync"/> hooks exactly once per exchange,
    /// after the final response has been fully written — called at the end of each transport's
    /// successful send path (buffered and streamed-finalize). Never called for an aborted or
    /// taken-over exchange, which has no final response to observe. A no-op on the
    /// zero-interceptor fast path.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the hooks' work.</param>
    internal async ValueTask InvokeAfterResponseAsync(CancellationToken cancellationToken)
    {
        if (_responseInterceptors is null || _responseInterception is null || _afterResponseInvoked)
        {
            return;
        }

        _afterResponseInvoked = true;

        foreach (IHttpResponseInterceptor interceptor in _responseInterceptors)
        {
            await interceptor.AfterResponseAsync(_responseInterception, cancellationToken).ConfigureAwait(false);
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
