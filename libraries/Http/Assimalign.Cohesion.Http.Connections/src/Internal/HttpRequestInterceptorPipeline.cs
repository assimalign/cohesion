using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Runs the listener's request-parse interceptors over a request head the transport has just
/// decoded (<see cref="TransportHttpRequestHead"/>), before the exchange context that will own the
/// request exists. The HTTP/2 and HTTP/3 transports call it at their context-construction sites.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP/1.1 parser (<see cref="Http1MessageReader"/>) invokes the same seam inline on
/// its own read path; this helper reproduces its ordering, CONNECT-skip, empty-body, freeze, and
/// failure-path disposal semantics for the multiplexed transports, keeping the seam contract uniform
/// across protocols. Per-protocol timing (documented on
/// <see cref="IHttpExchangeInterceptor"/>): HTTP/2 dispatches at <c>END_HEADERS</c> with a
/// streaming body, so hooks run before the application observes any body octet (DATA already
/// received sits buffered in the stream's flow-control-bounded pipe); HTTP/3 dispatches at the
/// request's HEADERS frame and reads the body lazily, so hooks run before any body octet is read.
/// Body hooks therefore wrap a forward-only stream that may still be arriving — exactly what the
/// hook contract requires wrappers to tolerate.
/// </para>
/// <para>
/// Freeze timing follows the body, not the protocol: a transport body that is read lazily and
/// enforces the cap itself (<see cref="IHttpLazyRequestBody"/> — HTTP/3) receives the parse context
/// and freezes the knob at its first read, exactly like HTTP/1.1; any other body (HTTP/2) has the
/// knob frozen here, after the head hooks, and its transport enforces the value
/// <see cref="InterceptAsync"/> returns.
/// </para>
/// <para>
/// Zero registered interceptors is the fast path: no interception context, no feature collection,
/// and no per-request allocation — the head's body flows through unchanged, exactly as before the
/// seam was wired into these transports.
/// </para>
/// </remarks>
internal static class HttpRequestInterceptorPipeline
{
    /// <summary>
    /// Invokes the head and body hooks for <paramref name="head"/> and returns what the exchange is
    /// built from: the hook-populated feature collection, the effective request-body cap — the parse
    /// context's knob as frozen after the head hooks — and the effective body stream. A lazy body
    /// (<see cref="IHttpLazyRequestBody"/>) is handed the parse context instead and freezes the knob
    /// at its first read, so for it the returned cap is only the knob's value when the hooks
    /// finished; the body enforces the value frozen at that read.
    /// </summary>
    /// <param name="interceptors">The listener's snapshotted request-parse interceptors.</param>
    /// <param name="version">The HTTP version of the exchange.</param>
    /// <param name="head">
    /// The decoded request head. Its <see cref="TransportHttpRequestHead.Body"/> is the transport body
    /// the body hooks wrap (unless the request is a CONNECT).
    /// </param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="maxRequestBodySize">The registration's body-size cap seeded into the parse context.</param>
    /// <param name="isConnect">
    /// Whether the request is a CONNECT, whose post-head octets are tunnel traffic rather than a
    /// message body; body hooks are skipped when <see langword="true"/>.
    /// </param>
    /// <returns>
    /// The hook-populated feature collection (<see langword="null"/> on the zero-interceptor fast
    /// path), the effective cap (<paramref name="maxRequestBodySize"/> unchanged on the fast path),
    /// and the body the request exposes: the outermost wrapper the body hooks produced, or the head's
    /// own body on the fast path and for a CONNECT.
    /// </returns>
    /// <exception cref="Assimalign.Cohesion.Http.HttpRequestRejectedException">
    /// Thrown when an interceptor rejects the request, after the partially-built body wrapper chain
    /// and every hook-attached feature have been disposed, since no exchange context will ever exist
    /// to own their disposal walk.
    /// </exception>
    public static async ValueTask<HttpRequestInterceptionResult> InterceptAsync(
        IHttpExchangeInterceptor[] interceptors,
        HttpVersion version,
        TransportHttpRequestHead head,
        HttpConnectionInfo connectionInfo,
        long? maxRequestBodySize,
        bool isConnect)
    {
        // Zero registered interceptors keeps the exact pre-seam fast path: no context, no feature
        // collection, no hook dispatch, and the request keeps its original body stream. The cap is
        // the registration's configured limit, untouched by any hook.
        if (interceptors.Length == 0)
        {
            return new HttpRequestInterceptionResult(null, maxRequestBodySize, head.Body);
        }

        HttpFeatureCollection features = new();
        HttpExchangeInterceptorRequestContext context = new()
        {
            Version = version,
            Method = head.Method,
            Path = head.Path,
            Scheme = head.Scheme,
            Host = head.Host,
            // Hooks observe headers through a read-only view; derived values belong in Features.
            Headers = head.Headers.AsReadOnly(),
            Features = features,
            ConnectionInfo = connectionInfo,
            MaxRequestBodySize = maxRequestBodySize,
        };

        // Tracks the outermost body stream produced so far so the failure path can tear down the
        // partial wrapper chain (the outermost wrapper owns the streams it wraps).
        Stream body = head.Body;

        try
        {
            // Head hooks: attach features and adjust the body-size knob before the body is exposed.
            foreach (IHttpExchangeInterceptor interceptor in interceptors)
            {
                interceptor.AfterRequestHead(context);
            }

            // The head hooks have run. A lazy transport body that enforces the cap itself (HTTP/3)
            // takes the context and freezes the knob at its first read — the h1 timing contract, so
            // BeforeRequestBody hooks and middleware keep the pre-read override window. Any other
            // body (HTTP/2's flow-controlled pipe, whose transport enforces the cap on receipt rather
            // than at the reader's pace) has the knob frozen here, so the effective value is fixed
            // for the remainder of the exchange (write-through features observe the freeze
            // immediately) and returned to the transport below.
            if (head.Body is IHttpLazyRequestBody lazyBody)
            {
                lazyBody.AttachInterception(context);
            }
            else
            {
                context.FreezeMaxRequestBodySize();
            }

            // Body hooks chain in registration order — each receives the previous result, so the
            // last registered interceptor produces the outermost wrapper. CONNECT tunnels are
            // skipped (post-head octets are tunnel traffic, not a message body); empty bodies still
            // run so wrappers over the (empty) representation stay meaningful.
            if (!isConnect)
            {
                // The body is about to be exposed and every head hook has run. The knob is frozen
                // unless the body is lazy (see above); on HTTP/2 octets may already sit buffered in
                // the flow-controlled pipe (see the per-protocol timing remarks above), so the hook
                // observes "before exposure".
                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    interceptor.BeforeRequestBody(context);
                }

                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    body = interceptor.AfterRequestBody(context, body);
                }
            }

            // Unless the body is lazy, the knob was frozen after the head hooks, so this is the value
            // the transport enforces for the rest of the exchange. A lazy body resolves and enforces
            // its own value at its first read.
            return new HttpRequestInterceptionResult(features, context.MaxRequestBodySize, body);
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
