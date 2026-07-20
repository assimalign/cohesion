using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal static class Http1MessageReader
{
    /// <summary>
    /// Reads a single HTTP/1.1 request from the wire, enforcing the configured server limits,
    /// invoking the registered request-parse interceptors, and driving the read-timeout phase
    /// transitions.
    /// </summary>
    /// <param name="stream">The connection stream to read from.</param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="scheme">The connection scheme (derived from the transport's security).</param>
    /// <param name="limits">The server limits enforced on the request line, headers, and body.</param>
    /// <param name="interceptors">
    /// The listener's snapshotted request-parse interceptors. When empty the parser takes a fast
    /// path with no per-request interception state allocated.
    /// </param>
    /// <param name="timeProvider">The monotonic clock threaded to the request body's data-rate enforcement.</param>
    /// <param name="readTimeout">
    /// The read-timeout controller. Signalled when the request line begins and once the header
    /// section has been fully received; its token bounds every head read.
    /// </param>
    /// <param name="connectionToken">
    /// The ambient connection token used as the request's abort token and to bound the body read.
    /// Distinct from <paramref name="readTimeout"/>'s token, which is disposed once the head has been
    /// read.
    /// </param>
    /// <returns>The parsed request context (dispatched at head; its body is read lazily), or <see langword="null"/> on a clean end-of-stream.</returns>
    /// <exception cref="Http1LimitExceededException">
    /// Thrown when the request head violates a configured limit (414 / 431).
    /// </exception>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown when an interceptor rejects the request (4xx / 5xx).
    /// </exception>
    public static async ValueTask<Http1Context?> ReadRequestAsync(
        Stream stream,
        HttpConnectionInfo connectionInfo,
        HttpScheme scheme,
        Http1ConnectionListenerOptions.Http1Limits limits,
        IHttpExchangeInterceptor[] interceptors,
        TimeProvider timeProvider,
        Http1ReadTimeout readTimeout,
        CancellationToken connectionToken)
    {
        CancellationToken readToken = readTimeout.Token;

        // RFC 9112 §3 — the request line is size-bounded; an over-long line is 414 (RFC 9110
        // §15.5.15). Signal the timeout controller on the first byte so the keep-alive idle window
        // tightens to the request-headers deadline (Slowloris defence).
        string? requestLine = await ReadLineAsync(
            stream,
            limits.MaxRequestLineSize,
            HttpStatusCode.RequestUriTooLong,
            "request line",
            readTimeout.OnRequestLineStarted,
            readToken).ConfigureAwait(false);

        if (requestLine is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        string[] requestLineParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (requestLineParts.Length != 3)
        {
            throw new InvalidDataException($"The HTTP/1.1 request line '{requestLine}' is invalid.");
        }

        if (!string.Equals(requestLineParts[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The HTTP version '{requestLineParts[2]}' is not supported by the HTTP/1.1 transport.");
        }

        HttpMethod method = HttpMethod.GetCanonicalizedValue(requestLineParts[0]);
        // RFC 9112 §3.2 — parse the request-target into one of the four canonical forms
        // (origin / absolute / authority / asterisk) with method/form pairing enforcement.
        if (!HttpRequestTarget.TryParse(requestLineParts[1], method, out HttpRequestTarget target, out string? targetError))
        {
            throw new InvalidDataException(
                $"The HTTP/1.1 request line '{requestLine}' has a malformed request-target: {targetError}");
        }

        // RFC 3986 §2.4 / RFC 9112 §3.2.1 — percent-decode the origin-form request-target path so this
        // transport surfaces IHttpRequest.Path identically to HTTP/2 and HTTP/3, which run the :path
        // through HttpPath.FromUriComponent (Http2Stream.ParseQuery / Http3HeaderCodec.ParseQuery).
        // HttpRequestTarget already split the path from the query on the first '?', so the query is
        // decoded independently (below, via HttpQuery.Parse — the same path h2/h3 take) and the decode
        // scopes to the path component only. FromUriComponent keeps %2F encoded (it never becomes a
        // separator — the routing '{**}' identity invariant relies on it), decodes encoded dot segments
        // to "..", and leaves invalid/overlong sequences intact per UrlDecoder. A decoded octet that is
        // not a legal path character (a space, control, '?'/'#', or NUL) makes the target malformed —
        // the same outcome the shared decode reaches on h2/h3; surface it as the transport's existing
        // malformed-request-target failure rather than letting it escape the read loop. The other three
        // request-target forms keep their current handling: absolute-form's path is already normalized by
        // System.Uri, authority-form (CONNECT) carries none, and asterisk-form is the literal '*'.
        HttpPath requestPath;
        if (target.Form == HttpRequestTargetForm.Origin)
        {
            try
            {
                requestPath = HttpPath.FromUriComponent(target.Path.Value);
            }
            catch (Exception ex) when (ex is HttpException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"The HTTP/1.1 request line '{requestLine}' has a malformed request-target: {ex.Message}");
            }
        }
        else
        {
            requestPath = target.Path;
        }

        HttpHeaderCollection headers = await ReadHeadersAsync(stream, limits, readToken).ConfigureAwait(false);

        // The header section is fully received; disarm the request-headers deadline so the body
        // read is not bounded by it. The body is read lazily after dispatch, bounded instead by the
        // connection token and (when configured) the minimum request-body data rate enforced by
        // Http1RequestBodyStream.
        readTimeout.OnHeadReceived();

        // RFC 9110 §9.3.6 — CONNECT requests MUST NOT consume octets past the request
        // headers; the bytes that follow belong to the tunnel. Detect this before any
        // body framing logic kicks in. We only care about the framing decision here;
        // exposing a typed protocol-upgrade feature is the Assimalign.Cohesion.Http.ProtocolUpgrade
        // package's job and requires a separate transport <-> ProtocolUpgrade bridge
        // (tracked as follow-up).
        bool isConnectTunnel = method == HttpMethod.Connect && target.Form == HttpRequestTargetForm.Authority;

        // Host resolution depends on the request-target form (RFC 9112 §3.2.2 / §3.2.3):
        //   - absolute-form  → authority component of the target supersedes any Host header
        //   - authority-form → the target itself IS the authority (CONNECT)
        //   - origin-form / asterisk → fall back to the Host header
        // Derived before the interceptor phase because the interception context carries them;
        // hooks observe headers through a read-only view, so the derivations cannot go stale.
        HttpHost host = target.Form switch
        {
            HttpRequestTargetForm.Absolute => target.Host,
            HttpRequestTargetForm.Authority => target.Host,
            _ => headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue hostValue)
                ? new HttpHost(hostValue.Value)
                : HttpHost.Empty,
        };

        // Scheme follows the same precedence: absolute-form carries it on the wire.
        HttpScheme requestScheme = target.Form == HttpRequestTargetForm.Absolute
            ? target.Scheme
            : scheme;

        // Interceptor phase (head hooks). Zero registered interceptors is the fast path: no
        // interception context, no feature collection, no per-request interception allocations —
        // the transport enforces the listener-wide limits exactly as before the seam existed.
        HttpFeatureCollection? features = null;
        HttpExchangeInterceptorRequestContext? interception = null;

        if (interceptors.Length > 0)
        {
            features = new HttpFeatureCollection();
            interception = new HttpExchangeInterceptorRequestContext
            {
                Version = HttpVersion.Http11,
                Method = method,
                Path = requestPath,
                Scheme = requestScheme,
                Host = host,
                Headers = headers.AsReadOnly(),
                Features = features,
                ConnectionInfo = connectionInfo,
                MaxRequestBodySize = limits.MaxRequestBodySize,
            };
        }

        Http1RequestBodyStream? requestBody = null;
        Stream? bodyStream = null;

        try
        {
            if (interception is not null)
            {
                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    interceptor.AfterRequestHead(interception);
                }
            }

            // RFC 9112 §6 / §7 — decide the body framing from the headers, rejecting ambiguous
            // combinations and malformed Content-Length / chunked encodings up front (before
            // dispatch). CONNECT carries no framed body (RFC 9110 §9.3.6 — post-header octets are
            // tunnel traffic). The body-size cap is NOT enforced here: an endpoint or middleware may
            // still adjust it after dispatch, before the body is read, so Http1RequestBodyStream
            // freezes and enforces it at the first read.
            Http1RequestBodyFraming framing = isConnectTunnel
                ? Http1RequestBodyFraming.None
                : Http1MessageBodyReader.DetermineFraming(headers);

            // Hook: the request is about to be dispatched with its lazy body — head hooks done,
            // framing decided, no body octet consumed. Runs before any Expect: 100-continue
            // solicitation (itself lazy, at the first body read), so a hook that rejects here does
            // so before the body is solicited from the peer. The body-size knob is deliberately not
            // yet frozen: it freezes at the first body read, which is what opens the pre-read
            // override window to middleware and endpoints. CONNECT tunnels skip it (their post-head
            // octets are tunnel traffic, not a message body).
            if (interception is not null && !isConnectTunnel)
            {
                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    interceptor.BeforeRequestBody(interception);
                }
            }

            // RFC 9112 §7.1.2 — only a chunked request can carry a trailer section. Surface a
            // supported (initially empty) trailer collection the body stream fills once the body is
            // fully read; non-chunked requests keep the shared unsupported collection.
            HttpTrailerCollection trailers = framing.Mode == Http1RequestBodyMode.Chunked
                ? new HttpTrailerCollection(isSupported: true)
                : HttpTrailerCollection.Unsupported;

            // RFC 9110 §10.1.1 — a request that declares "Expect: 100-continue" with a framed body
            // withholds the body until the server solicits it. The body streams after dispatch, so
            // the solicitation is lazy and application-driven: Http1RequestBodyStream emits
            // 100 Continue at the first body read (the shape the buffered reader explicitly
            // de-scoped to this rework). A handler can thus answer 401/417 without soliciting the
            // body at all; an exchange whose declared-and-never-solicited body is left unread closes
            // the connection instead of reusing it (see Http1RequestBodyStream.DrainAsync).
            bool solicitContinue = !isConnectTunnel && ShouldSolicitContinue(headers);

            // The lazy body stream: read incrementally after dispatch, enforcing the (frozen-at-
            // first-read) body-size cap and the minimum request-body data rate. No body byte is read
            // here — the request is dispatched at head.
            requestBody = new Http1RequestBodyStream(
                stream,
                framing,
                solicitContinue,
                interception,
                limits.MaxRequestBodySize,
                limits.MinRequestBodyDataRate,
                timeProvider,
                connectionToken,
                trailers);
            bodyStream = requestBody;

            // Interceptor phase (body hooks): each hook receives the previous result, so the last
            // registered interceptor produces the outermost wrapper. CONNECT tunnels are skipped —
            // the post-head octets are tunnel traffic, not a message body — but empty bodies still
            // run so wrappers over the (empty) representation stay meaningful.
            if (interception is not null && !isConnectTunnel)
            {
                foreach (IHttpExchangeInterceptor interceptor in interceptors)
                {
                    bodyStream = interceptor.AfterRequestBody(interception, bodyStream);
                }
            }

            HttpQueryCollection queryCollection = new HttpQuery(target.Query.Value).Parse();

            Http1Request request = new(
                host,
                requestPath,
                method,
                requestScheme,
                queryCollection,
                headers,
                bodyStream,
                trailers);
            Http1Response response = new();

            bool keepAlive = !HeaderContainsToken(headers, HttpHeaderKey.Connection, "close");

            return new Http1Context(
                request,
                response,
                connectionInfo,
                connectionToken,
                keepAlive,
                requestBody,
                features);
        }
        catch when (features is not null)
        {
            // The request failed after interceptors started participating (a head- or body-hook
            // rejection, or malformed framing) and no Http1Context — the owner of the feature-
            // disposal walk — will ever exist for this exchange. Honor the seam's disposal contract
            // here instead: tear down the partially-built wrapper chain (the outermost wrapper owns
            // its inner stream) and dispose every hook-attached feature, then let the failure surface
            // unchanged. The exception filter keeps the zero-interceptor fast path entirely outside
            // this handler.
            bodyStream?.Dispose();
            await DisposeFeaturesAsync(features).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Best-effort disposal of hook-attached features for a request that failed before its
    /// <see cref="Http1Context"/> was constructed. Mirrors the exchange's normal disposal walk
    /// (snapshot first; prefer <see cref="IAsyncDisposable"/>; one throwing feature does not
    /// abort the rest).
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

    private static async ValueTask<HttpHeaderCollection> ReadHeadersAsync(
        Stream stream,
        Http1ConnectionListenerOptions.Http1Limits limits,
        CancellationToken cancellationToken)
    {
        HttpHeaderCollection headers = new();

        int headerCount = 0;
        int totalBytesRemaining = limits.MaxRequestHeadersTotalSize;

        while (true)
        {
            // The per-line cap is the remaining header budget: a single line can never push the
            // header section past its total-size bound (RFC 9110 §15.5.22 → 431).
            string? line = await ReadLineAsync(
                stream,
                totalBytesRemaining,
                HttpStatusCode.RequestHeaderFieldsTooLarge,
                "request header section",
                onFirstByte: null,
                cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new EndOfStreamException("The connection closed before the request headers were fully received.");
            }

            if (line.Length == 0)
            {
                return headers;
            }

            // Account this field line (and its CRLF) against the total header-section budget.
            int consumed = line.Length + 2;
            if (consumed > totalBytesRemaining)
            {
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"The request header section exceeds the configured maximum size ({limits.MaxRequestHeadersTotalSize} octets).");
            }
            totalBytesRemaining -= consumed;

            headerCount++;
            if (headerCount > limits.MaxRequestHeaderCount)
            {
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"The request contains more than the configured maximum of {limits.MaxRequestHeaderCount} header fields.");
            }

            int separatorIndex = line.IndexOf(':');

            if (separatorIndex <= 0)
            {
                throw new InvalidDataException($"The HTTP header '{line}' is invalid.");
            }

            string name = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            HttpHeaderKey key = new(name);

            if (headers.TryGetValue(key, out HttpHeaderValue existingValue))
            {
                headers[key] = HttpHeaderValue.Concat(existingValue, value);
            }
            else
            {
                headers[key] = value;
            }
        }
    }

    /// <summary>
    /// Whether the transport should automatically emit <c>100 Continue</c> before reading the body:
    /// the request declares <c>Expect: 100-continue</c> (RFC 9110 §10.1.1) and carries a framing that
    /// indicates a message body. Absent the expectation, or with no body to solicit, no interim
    /// response is sent.
    /// </summary>
    private static bool ShouldSolicitContinue(HttpHeaderCollection headers)
    {
        return HeaderContainsToken(headers, HttpHeaderKey.Expect, "100-continue")
            && RequestHasBody(headers);
    }

    /// <summary>
    /// Whether the request's framing indicates a message body (RFC 9112 §6): a <c>Transfer-Encoding</c>
    /// header, or a <c>Content-Length</c> with a non-zero value. A <c>Content-Length: 0</c> (or an
    /// absent length with no transfer coding) indicates no body, so <c>100 Continue</c> is not
    /// solicited for it.
    /// </summary>
    private static bool RequestHasBody(HttpHeaderCollection headers)
    {
        if (headers.ContainsKey(HttpHeaderKey.TransferEncoding))
        {
            return true;
        }

        if (headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue contentLength))
        {
            foreach (string? entry in contentLength)
            {
                if (entry is null)
                {
                    continue;
                }

                foreach (string segment in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // Any non-"0" segment means a body is expected. A malformed value also lands here
                    // and is rejected by the body reader afterward; soliciting first is harmless.
                    if (!string.Equals(segment, "0", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HeaderContainsToken(HttpHeaderCollection headers, HttpHeaderKey key, string expected)
    {
        if (!headers.TryGetValue(key, out HttpHeaderValue value))
        {
            return false;
        }

        foreach (string? entry in value)
        {
            if (entry is null)
            {
                continue;
            }

            string[] segments = entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string segment in segments)
            {
                if (string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a single CRLF-terminated line, capping its length so an unbounded line cannot be
    /// buffered into an ever-growing <see cref="MemoryStream"/> (a live memory-exhaustion vector).
    /// </summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxLineSize">The maximum number of payload octets the line may contain.</param>
    /// <param name="overflowStatus">The HTTP status to reject with when the cap is exceeded.</param>
    /// <param name="subject">A short description of the line for the rejection message.</param>
    /// <param name="onFirstByte">
    /// Invoked once when the first byte of the line is read; used to signal the read-timeout phase
    /// transition. May be <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The decoded line, or <see langword="null"/> on a clean end-of-stream before any byte.</returns>
    /// <exception cref="Http1LimitExceededException">Thrown when the line exceeds <paramref name="maxLineSize"/>.</exception>
    private static async ValueTask<string?> ReadLineAsync(
        Stream stream,
        int maxLineSize,
        HttpStatusCode overflowStatus,
        string subject,
        Action? onFirstByte,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        bool sawCarriageReturn = false;
        bool signaledFirstByte = false;

        while (true)
        {
            int value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                if (buffer.Length == 0 && !sawCarriageReturn)
                {
                    return null;
                }

                throw new EndOfStreamException("The connection closed while an HTTP line was being read.");
            }

            if (!signaledFirstByte)
            {
                signaledFirstByte = true;
                onFirstByte?.Invoke();
            }

            if (sawCarriageReturn)
            {
                if (value == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }

                AppendByte(buffer, (byte)'\r', maxLineSize, overflowStatus, subject);
                sawCarriageReturn = false;
            }

            if (value == '\r')
            {
                sawCarriageReturn = true;
                continue;
            }

            AppendByte(buffer, (byte)value, maxLineSize, overflowStatus, subject);
        }
    }

    private static void AppendByte(MemoryStream buffer, byte value, int maxLineSize, HttpStatusCode overflowStatus, string subject)
    {
        if (buffer.Length >= maxLineSize)
        {
            throw new Http1LimitExceededException(
                overflowStatus,
                $"The {subject} exceeds the configured maximum size ({maxLineSize} octets).");
        }

        buffer.WriteByte(value);
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return bytesRead == 0 ? -1 : buffer[0];
    }
}
