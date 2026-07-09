using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Runs the listener's request-parse interceptors over a request whose head has just been
/// assembled into a materialized <see cref="TransportHttpRequest"/> — the shape both the HTTP/2
/// and HTTP/3 transports present at their context-construction sites.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP/1.1 parser (<see cref="Http1.Http1MessageReader"/>) invokes the same seam inline on
/// its own read path; this helper reproduces its ordering, CONNECT-skip, empty-body, freeze, and
/// failure-path disposal semantics for the transports that hand over a completed request object,
/// keeping the seam contract uniform across protocols. Per-protocol timing (documented on
/// <see cref="IHttpExchangeInterceptor"/>): HTTP/2 dispatches at <c>END_HEADERS</c> with a
/// streaming body, so hooks run before the application observes any body octet (DATA already
/// received sits buffered in the stream's flow-control-bounded pipe); HTTP/3 drains the request
/// stream before header decode, so hooks run before the body is <em>exposed</em> but not before
/// it was <em>received</em>. Body hooks therefore wrap a forward-only stream that may still be
/// arriving — exactly what the hook contract requires wrappers to tolerate.
/// </para>
/// <para>
/// Zero registered interceptors is the fast path: no interception context, no feature collection,
/// and no per-request allocation — the request flows through with its original body stream, exactly
/// as before the seam was wired into these transports.
/// </para>
/// </remarks>
internal static class HttpRequestInterceptorPipeline
{
    /// <summary>
    /// Invokes the head and body hooks for <paramref name="request"/> and returns the
    /// hook-populated feature collection to flow into the exchange, or <see langword="null"/> when
    /// no interceptors are registered.
    /// </summary>
    /// <param name="interceptors">The listener's snapshotted request-parse interceptors.</param>
    /// <param name="version">The HTTP version of the exchange.</param>
    /// <param name="request">
    /// The decoded request. Its <see cref="TransportHttpRequest.Body"/> is replaced with the
    /// wrapped stream produced by the body hooks (unless the request is a CONNECT).
    /// </param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="maxRequestBodySize">
    /// The registration's body-size cap seeded into the parse context. Interceptors may adjust it
    /// until it is frozen after the head hooks; on these transports the value is carried for
    /// hook-attached features (h2 bounds body buffering via flow-control backpressure, h3 via QUIC
    /// flow control) — the hard wire-level cap remains tracked follow-up work.
    /// </param>
    /// <param name="isConnect">
    /// Whether the request is a CONNECT, whose post-head octets are tunnel traffic rather than a
    /// message body; body hooks are skipped when <see langword="true"/>.
    /// </param>
    /// <returns>
    /// The feature collection populated by the head hooks, or <see langword="null"/> for the
    /// zero-interceptor fast path.
    /// </returns>
    /// <exception cref="Assimalign.Cohesion.Http.HttpRequestRejectedException">
    /// Thrown when an interceptor rejects the request. Before it surfaces, the partially-built body
    /// wrapper chain and every hook-attached feature are disposed, since no exchange context will
    /// ever exist to own their disposal walk.
    /// </exception>
    public static async ValueTask<HttpFeatureCollection?> InvokeAsync(
        IHttpExchangeInterceptor[] interceptors,
        HttpVersion version,
        TransportHttpRequest request,
        HttpConnectionInfo connectionInfo,
        long? maxRequestBodySize,
        bool isConnect)
    {
        // Zero registered interceptors keeps the exact pre-seam fast path: no context, no feature
        // collection, no hook dispatch, and the request keeps its original body stream.
        if (interceptors.Length == 0)
        {
            return null;
        }

        HttpFeatureCollection features = new();
        HttpRequestInterceptorContext context = new()
        {
            Version = version,
            Method = request.Method,
            Path = request.Path,
            Scheme = request.Scheme,
            Host = request.Host,
            // Hooks observe headers through a read-only view; derived values belong in Features.
            Headers = request.Headers.AsReadOnly(),
            Features = features,
            ConnectionInfo = connectionInfo,
            MaxRequestBodySize = maxRequestBodySize,
        };

        // Tracks the outermost body stream produced so far so the failure path can tear down the
        // partial wrapper chain (the outermost wrapper owns the streams it wraps).
        Stream body = request.Body;

        try
        {
            // Head hooks: attach features and adjust the body-size knob before the body is exposed.
            foreach (IHttpExchangeInterceptor interceptor in interceptors)
            {
                interceptor.AfterRequestHead(context);
            }

            // The head hooks have run; freeze the knob so the effective cap is fixed for the
            // remainder of the exchange (write-through features observe the freeze immediately).
            // Matches the h1 timing contract — on the buffered transports there is no wire read to
            // begin, so the freeze happens here rather than at the first body byte.
            context.FreezeMaxRequestBodySize();

            // Body hooks chain in registration order — each receives the previous result, so the
            // last registered interceptor produces the outermost wrapper. CONNECT tunnels are
            // skipped (post-head octets are tunnel traffic, not a message body); empty bodies still
            // run so wrappers over the (empty) representation stay meaningful.
            if (!isConnect)
            {
                // The body is about to be exposed — the effective knobs are frozen and every head
                // hook has run. On these transports the octets may already sit buffered (see the
                // per-protocol timing remarks above); the hook observes "before exposure".
                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    interceptor.BeforeRequestBody(context);
                }

                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    body = interceptor.AfterRequestBody(context, body);
                }

                request.Body = body;
            }

            return features;
        }
        catch
        {
            // The request failed while interceptors were participating (a hook rejection or any
            // other hook fault) and no exchange context — the owner of the feature-disposal walk —
            // will ever exist. Honor the seam's disposal contract here instead: tear down the
            // partially-built wrapper chain and dispose every hook-attached feature, then let the
            // failure surface unchanged.
            body?.Dispose();
            await DisposeFeaturesAsync(features).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Best-effort disposal of hook-attached features for a request that failed before its exchange
    /// context was constructed. Mirrors the exchange's normal disposal walk (snapshot first; prefer
    /// <see cref="IAsyncDisposable"/>; one throwing feature does not abort the rest).
    /// </summary>
    private static async ValueTask DisposeFeaturesAsync(HttpFeatureCollection features)
    {
        IHttpFeature[] snapshot = [.. features];

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
                // Best-effort: cleanup of one feature must not mask the original failure or
                // prevent the remaining features from being disposed.
            }
        }
    }
}
