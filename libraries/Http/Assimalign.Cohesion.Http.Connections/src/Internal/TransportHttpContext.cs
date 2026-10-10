using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The exchange context every HTTP version's transport derives from.
/// </summary>
/// <remarks>
/// The context constructs its own request and response and passes itself to each, so their
/// <see cref="HttpRequest.HttpContext"/> / <see cref="HttpResponse.HttpContext"/> back-references
/// are fixed at construction (#699). A transport therefore hands the constructor the decoded
/// <see cref="TransportHttpRequestHead"/>, after the request-parse interceptors have run over it,
/// rather than a finished request.
/// </remarks>
internal abstract class TransportHttpContext : HttpContext
{
    // Backs RequestAborted. Linked to the transport-supplied token(s) so the
    // exchange is aborted when the connection/stream is torn down, and can also
    // be tripped locally by Cancel().
    private readonly CancellationTokenSource _abortedSource;

    /// <summary>
    /// Initializes the exchange: its request, its response, and the source behind
    /// <see cref="RequestCancelled"/>.
    /// </summary>
    /// <param name="version">The exchange's protocol version.</param>
    /// <param name="requestHead">The parsed request head.</param>
    /// <param name="connectionInfo">The connection's endpoints.</param>
    /// <param name="requestAborted">The transport's token that aborts the exchange.</param>
    /// <param name="featureCapacity">
    /// The number of features the exchange is expected to carry, which sizes the feature collection
    /// this constructor creates when <paramref name="features"/> is <see langword="null"/>.
    /// </param>
    /// <param name="features">The features request-parse interceptors attached, or <see langword="null"/>.</param>
    /// <param name="streamAborted">
    /// A second transport token that aborts the exchange — the HTTP/3 request stream's
    /// <c>ConnectionClosed</c>, which fires when the client resets or stops the stream — or
    /// <see langword="default"/> for none. Both tokens feed one linked source.
    /// </param>
    protected TransportHttpContext(
        HttpVersion version,
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        int featureCapacity,
        IHttpFeatureCollection? features = null,
        CancellationToken streamAborted = default)
    {
        Version = version;
        // The request and response only store the reference; neither calls back into this context
        // while it is still being constructed. HTTP/2 and HTTP/3 send response trailers as a trailing
        // HEADERS frame (RFC 9113 §8.1, RFC 9114 §4.1); HTTP/1.1 does not (decision 18), and neither
        // does a CONNECT exchange, whose stream carries only DATA once the tunnel is up (RFC 9113
        // §8.5, RFC 9114 §4.4).
        Request = new TransportHttpRequest(this, requestHead);
        Response = new TransportHttpResponse(
            this,
            supportsTrailers: (version is HttpVersion.Http20 or HttpVersion.Http30) && requestHead.Method != HttpMethod.Connect);
        ConnectionInfo = connectionInfo;
        _abortedSource = streamAborted.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(requestAborted, streamAborted)
            : CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        // The parser pre-populates the feature collection when request-parse interceptors
        // attached features during the read (see IHttpExchangeInterceptor); it is used directly —
        // no defaults-wrapper layer, which would add a second dictionary probe to every Get on
        // the hot path. A null/foreign collection degrades gracefully: null gets a fresh empty
        // collection sized for the features the exchange is expected to carry (the zero-interceptor
        // fast path), and a non-HttpFeatureCollection implementation is wrapped as a read-through
        // defaults source. On disposal the effective collection is walked and every feature
        // implementing IDisposable / IAsyncDisposable is disposed.
        Features = features switch
        {
            null => new HttpFeatureCollection(featureCapacity),
            HttpFeatureCollection concrete => concrete,
            _ => new HttpFeatureCollection(features),
        };
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
    }

    public override HttpVersion Version { get; }
    public override TransportHttpRequest Request { get; }
    public override TransportHttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo { get; }
    public override HttpFeatureCollection Features { get; }
    public override IDictionary<string, object?> Items { get; }
    public override CancellationToken RequestCancelled => _abortedSource.Token;

    // The response-lifecycle interception state, retained for the exchange's lifetime when at
    // least one response interceptor is registered so the later lifecycle hooks
    // (BeforeResponseHeadAsync / AfterResponseAsync) re-invoke against the same context. Null on
    // the zero-interceptor fast path, which keeps the later invokers true no-ops.
    private IHttpExchangeInterceptor[]? _responseInterceptors;
    private HttpExchangeInterceptorResponseContext? _responseInterception;
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
    /// application and the <see cref="IHttpExchangeControl"/> transitions drive: <see cref="Cancel"/>
    /// maps to <see cref="HttpExchangeDirective.Abort"/>; a protocol that supports handing off its
    /// connection or stream overrides this to report <see cref="HttpExchangeDirective.TakeOver"/>
    /// (<see cref="IHttpExchangeControl.TakeOver"/> on <c>Http1Context</c>,
    /// <see cref="IHttpExchangeControl.AcceptTunnelAsync"/> on <c>Http2Context</c> and
    /// <c>Http3Context</c>).
    /// </summary>
    internal virtual HttpExchangeDirective ExchangeDirective =>
        CancelRequested ? HttpExchangeDirective.Abort : HttpExchangeDirective.Continue;

    /// <summary>
    /// The response interceptors a request-parse hook added to this exchange alone
    /// (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>), carried from the
    /// parse context to the exchange's setup; <see langword="null"/> when none was added.
    /// </summary>
    internal IReadOnlyList<IHttpExchangeInterceptor>? AddedResponseInterceptors { get; init; }

    /// <summary>
    /// Resolves the response interceptors that take part in this exchange: the listener's
    /// <paramref name="registered"/> ones, then any a request-parse hook added to this exchange, in
    /// the order added, each at most once. Returns <paramref name="registered"/> itself, allocating
    /// nothing, unless a hook added one, so an exchange no hook claimed keeps the fast path when the
    /// listener registered no response interceptor.
    /// </summary>
    /// <param name="registered">The listener's snapshotted response interceptors.</param>
    /// <returns>The interceptors to run the response phase with; empty for the buffered fast path.</returns>
    internal IHttpExchangeInterceptor[] ResolveResponseInterceptors(IHttpExchangeInterceptor[] registered)
    {
        if (AddedResponseInterceptors is not { Count: > 0 } added)
        {
            return registered;
        }

        List<IHttpExchangeInterceptor> effective = new(registered.Length + added.Count);
        effective.AddRange(registered);

        foreach (IHttpExchangeInterceptor interceptor in added)
        {
            if (!effective.Contains(interceptor))
            {
                effective.Add(interceptor);
            }
        }

        return effective.Count == registered.Length ? registered : [.. effective];
    }

    /// <summary>
    /// Runs the registered response interceptors' <see cref="IHttpExchangeInterceptor.BeforeResponse"/>
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
        IHttpExchangeInterceptor[] interceptors,
        HttpResponseBodyStream sink,
        IHttpExchangeControl control)
    {
        ResponseBodySink = sink;
        _responseInterceptors = interceptors;
        _responseInterception = new HttpExchangeInterceptorResponseContext
        {
            Version = Version,
            Headers = Response.Headers,
            Features = Features,
            ConnectionInfo = ConnectionInfo,
            ResponseBody = sink,
            Control = control,
        };

        foreach (IHttpExchangeInterceptor interceptor in interceptors)
        {
            interceptor.BeforeResponse(_responseInterception);
        }
    }

    /// <summary>
    /// Invokes the registered response interceptors'
    /// <see cref="IHttpExchangeInterceptor.BeforeResponseHeadAsync"/> hooks exactly once per
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

        foreach (IHttpExchangeInterceptor interceptor in _responseInterceptors)
        {
            await interceptor.BeforeResponseHeadAsync(_responseInterception, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes the registered response interceptors'
    /// <see cref="IHttpExchangeInterceptor.AfterResponseAsync"/> hooks exactly once per exchange,
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

        foreach (IHttpExchangeInterceptor interceptor in _responseInterceptors)
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
    /// parse time by a registered <see cref="IHttpExchangeInterceptor"/>
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
