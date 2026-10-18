using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal static class Http1MessageReader
{
    // These diagnostics also run during ordinary validation, cancellation or completion.
    private static readonly string _requestHeaderSectionSubject = ErrorMessages.RequestHeaderSection;
    private static readonly string _requestLineSubject = ErrorMessages.RequestLine;

    /// <summary>
    /// Reads a single HTTP/1.1 request from the wire, enforcing the configured server limits,
    /// invoking the registered request-parse interceptors, and driving the read-timeout phase
    /// transitions.
    /// </summary>
    /// <param name="input">
    /// The connection's pipe reader, which the request head is read from. The octets behind the head
    /// stay unconsumed in it, so <paramref name="stream"/> (an adapter over the same pipe) reads the
    /// body, and the next request or a taken-over stream starts where the head ended.
    /// </param>
    /// <param name="stream">The connection stream the request body is read from.</param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="scheme">The connection scheme (derived from the transport's security).</param>
    /// <param name="limits">The server limits enforced on the request line, headers, and body.</param>
    /// <param name="interceptors">
    /// The listener's snapshotted request-parse interceptors. When empty the parser takes a fast
    /// path with no per-request interception state allocated.
    /// </param>
    /// <param name="featureCapacity">
    /// The number of features each exchange is expected to carry; the exchange's feature collection
    /// is sized for it.
    /// </param>
    /// <param name="timeProvider">The monotonic clock threaded to the request body's data-rate enforcement.</param>
    /// <param name="readTimeout">
    /// The read-timeout controller. Signalled when the request line begins and once the header
    /// section has been fully received; its deadline bounds every head read.
    /// </param>
    /// <param name="connectionToken">
    /// The ambient connection token used as the request's abort token and to bound the body read.
    /// Distinct from <paramref name="readTimeout"/>'s token, which is disposed once the head has been
    /// read.
    /// </param>
    /// <param name="connectionClosed">The driver's closure token, linked for the exchange's lifetime.</param>
    /// <returns>The parsed request context (dispatched at head; its body is read lazily), or <see langword="null"/> on a clean end-of-stream.</returns>
    /// <exception cref="Http1LimitExceededException">
    /// Thrown when the request head violates a configured limit (414 / 431).
    /// </exception>
    /// <exception cref="Http1BadRequestException">
    /// Thrown when the request line holds an octet other than VCHAR and SP, a header field line is
    /// malformed (a name that is not a token, or a value with a control character other than HTAB), or
    /// a <c>Host</c> value holds an octet other than VCHAR (400).
    /// </exception>
    /// <exception cref="Http1MisdirectedRequestException">
    /// Thrown when an absolute-form request-target has the <c>https</c> scheme and the connection is not
    /// secured (421).
    /// </exception>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown when an interceptor rejects the request (4xx / 5xx).
    /// </exception>
    public static async ValueTask<Http1Context?> ReadRequestAsync(
        PipeReader input,
        Stream stream,
        HttpConnectionInfo connectionInfo,
        HttpScheme scheme,
        Http1ConnectionListenerOptions.Http1Limits limits,
        IHttpExchangeInterceptor[] interceptors,
        int featureCapacity,
        TimeProvider timeProvider,
        Http1ReadTimeout readTimeout,
        CancellationToken connectionToken,
        CancellationToken connectionClosed = default)
    {
        // RFC 9112 §3 — the request line is size-bounded; an over-long line is 414 (RFC 9110
        // §15.5.15). Signal the timeout controller on the first byte so the keep-alive idle window
        // tightens to the request-headers deadline (Slowloris defence).
        string? requestLine = await ReadLineAsync(
            input,
            limits.MaxRequestLineSize,
            HttpStatusCode.RequestUriTooLong,
            _requestLineSubject,
            readTimeout.OnRequestLineStarted,
            readTimeout).ConfigureAwait(false);

        if (requestLine is null)
        {
            return null;
        }

        // RFC 9112 §3 — a request line is method SP request-target SP HTTP-version, so every octet in
        // it is a VCHAR or SP. Anything else is answered with 400 before the line is split (#1341): an
        // octet above 0x7F was once decoded to '?', which split "/admin\xFFx" into the path "/admin"
        // and the query "x" while an intermediary forwarding the raw octets saw another path, and a
        // bare CR, a bare LF, or a tab is whitespace that another parser may split on.
        int invalidOctet = requestLine.AsSpan().IndexOfAnyExceptInRange(' ', '~');
        if (invalidOctet >= 0)
        {
            throw new Http1BadRequestException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1RequestLineCharacterInvalid, (int)requestLine[invalidOctet], invalidOctet));
        }

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        string[] requestLineParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (requestLineParts.Length != 3)
        {
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1RequestLineInvalid, requestLine));
        }

        if (!string.Equals(requestLineParts[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1VersionNotSupported, requestLineParts[2]));
        }

        // RFC 9112 §3 / RFC 9110 §9.1 — the method is a token. A VCHAR such as '@', '(' or ',' passes the
        // octet check above but is no tchar, and HttpMethod.GetCanonicalizedValue used to throw
        // ArgumentException for it out of the receive enumeration instead of answering. HTTP/2 and HTTP/3
        // refuse the same :method through HttpFieldNormalization.ValidateRequestTarget. The method is not
        // echoed: it came from the peer.
        string methodToken = requestLineParts[0];
        if (!HttpFieldNormalization.IsValidFieldName(methodToken))
        {
            throw new Http1BadRequestException(ErrorMessages.RequestMethodInvalid);
        }

        HttpMethod method;
        try
        {
            method = HttpMethod.GetCanonicalizedValue(methodToken);
        }
        catch (ArgumentException)
        {
            // A non-empty token is refused only when it is longer than the 32 characters HttpMethod holds.
            // RFC 9112 §3 — a server that receives a method longer than any that it implements SHOULD
            // respond with 501 (Not Implemented).
            throw new Http1LimitExceededException(
                HttpStatusCode.NotImplemented,
                ErrorMessages.RequestMethodTooLong);
        }

        // RFC 9112 §3.2 — parse the request-target into one of the four canonical forms
        // (origin / absolute / authority / asterisk) with method/form pairing enforcement.
        if (!HttpRequestTarget.TryParse(requestLineParts[1], method, out HttpRequestTarget target, out string? targetError))
        {
            throw new InvalidDataException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1RequestTargetInvalid, requestLine, targetError));
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
        // malformed-request-target failure rather than letting it escape the read loop.
        //
        // RFC 9112 §3.2.2 — absolute-form takes the same decode. HttpRequestTarget reads its path from
        // System.Uri.AbsolutePath, which removes dot segments and decodes escaped unreserved characters
        // but leaves every other escape in place ("/caf%C3%A9", "/100%25"), so without it a request that
        // names the same resource in absolute-form would reach middleware still encoded, and a redirect
        // that encodes the path would encode it twice. Authority-form (CONNECT) carries no path, and
        // asterisk-form is the literal '*'.
        HttpPath requestPath;
        if (target.Form is HttpRequestTargetForm.Origin or HttpRequestTargetForm.Absolute)
        {
            try
            {
                requestPath = HttpPath.FromUriComponent(target.Path.Value);
            }
            catch (Exception ex) when (ex is HttpException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1RequestTargetInvalid, requestLine, ex.Message));
            }
        }
        else
        {
            requestPath = target.Path;
        }

        HttpHeaderCollection headers = await ReadHeadersAsync(input, limits, readTimeout).ConfigureAwait(false);

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

        // RFC 9112 §3.2 — a Host field with an invalid value MUST be answered with 400. Host is
        // uri-host [":" port] (RFC 9110 §7.2), every octet of which is a VCHAR, so anything else makes
        // it invalid: an interior SP or HTAB, or obs-text. A no-break space would otherwise reach
        // HttpHost, where a host allowlist reading "api.test\xA0" might match "api.test" while a front
        // end that routes on the raw value sees another host (#1341).
        EnsureHostFieldIsVisible(headers);

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

        // Scheme follows the same precedence: absolute-form carries it on the wire, and it is the target
        // URI's scheme (RFC 9112 §3.3). An "https" target names a resource reachable only over a secured
        // connection, and an origin server MUST reject a request for one that arrives over a connection
        // that is not (RFC 9110 §4.2.2, §7.4): answer 421 (Misdirected Request) instead of serving it.
        // Taking the target's scheme as it is would mark a cleartext request secure, so
        // UseHttpsRedirection would pass it on, and HSTS and Secure-cookie decisions would read it as
        // https. An "http" target over TLS is kept as http: it claims less than the connection gives.
        if (target.Form == HttpRequestTargetForm.Absolute && target.Scheme == HttpScheme.Https && scheme != HttpScheme.Https)
        {
            throw new Http1MisdirectedRequestException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1RequestSchemeMismatch, target.RawValue));
        }

        HttpScheme requestScheme = target.Form == HttpRequestTargetForm.Absolute
            ? target.Scheme
            : scheme;

        // RFC 9112 §6 / §7 — decide the body framing from the headers, rejecting ambiguous
        // combinations and malformed Content-Length / chunked encodings up front (before
        // dispatch, and before any hook runs, so a hook never sees a request whose framing cannot be
        // read). CONNECT carries no framed body (RFC 9110 §9.3.6 — post-header octets are
        // tunnel traffic). The body-size cap is NOT enforced here: an endpoint or middleware may
        // still adjust it after dispatch, before the body is read, so Http1RequestBodyStream
        // freezes and enforces it at the first read.
        Http1RequestBodyFraming framing = isConnectTunnel
            ? Http1RequestBodyFraming.None
            : Http1MessageBodyReader.DetermineFraming(headers);

        // Interceptor phase (head hooks). Zero registered interceptors is the fast path: no
        // interception context, no feature collection, no per-request interception allocations —
        // the transport enforces the listener-wide limits exactly as before the seam existed. The
        // hooks fill the exchange's own collection, so it is sized for every feature the exchange is
        // expected to carry, not only the hooks' (on the fast path the exchange context creates and
        // sizes it).
        HttpFeatureCollection? features = null;
        HttpExchangeInterceptorRequestContext? interception = null;

        if (interceptors.Length > 0)
        {
            features = new HttpFeatureCollection(featureCapacity);
            interception = new HttpExchangeInterceptorRequestContext
            {
                Version = HttpVersion.Http11,
                Method = method,
                Path = requestPath,
                Scheme = requestScheme,
                Host = host,
                // RFC 9112 §6 — a body follows the head only when its framing declares one.
                CanHaveBody = framing.Mode == Http1RequestBodyMode.Chunked
                    || (framing.Mode == Http1RequestBodyMode.ContentLength && framing.ContentLength > 0),
                Headers = headers.AsReadOnly(),
                Features = features,
                ConnectionInfo = connectionInfo,
                MaxRequestBodySize = limits.MaxRequestBodySize,
                MinRequestBodyDataRate = limits.MinRequestBodyDataRate,
                MinResponseDataRate = limits.MinResponseDataRate,
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
            // first-read) body-size cap, the minimum request-body data rate, and the bounds on a
            // chunked body's framing lines and trailer section. No body byte is read here — the
            // request is dispatched at head.
            requestBody = new Http1RequestBodyStream(
                input,
                stream,
                framing,
                solicitContinue,
                interception,
                limits,
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

            TransportHttpRequestHead requestHead = new(
                host,
                requestPath,
                method,
                requestScheme,
                queryCollection,
                headers,
                bodyStream,
                trailers);

            bool keepAlive = !HeaderContainsToken(headers, HttpHeaderKey.Connection, "close");

            // A head hook that needs this exchange's response phase added itself to it; the
            // connection runs those hooks when it sets the exchange up.
            return new Http1Context(
                requestHead,
                connectionInfo,
                connectionToken,
                keepAlive,
                requestBody,
                featureCapacity,
                features,
                connectionClosed)
            {
                AddedResponseInterceptors = interception is { ResponseInterceptors.Count: > 0 } ? interception.ResponseInterceptors : null,
                RequestInterception = interception,
            };
        }
        catch (Exception exception) when (features is not null)
        {
            // The request failed after interceptors started participating (a head- or body-hook
            // rejection, including malformed body framing read by a hook) and no Http1Context —
            // the owner of the feature-disposal walk — will ever exist for this exchange. Honor the seam's
            // disposal contract here instead: tear down the partially-built wrapper chain (the outermost
            // wrapper owns its inner stream) and dispose every hook-attached feature, then let the failure
            // surface unchanged. The exception filter keeps the zero-interceptor fast path entirely outside
            // this handler.
            // Only an escaping framing failure needs replay here. If a hook replaces it with
            // an explicit rejection, the connection's error-response path reports that instead.
            if (exception is InvalidDataException)
            {
                requestBody?.ReportPendingMalformedRejection(
                    connectionInfo is HttpTransportConnectionInfo info ? info.ConnectionId : default);
            }
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
        PipeReader input,
        Http1ConnectionListenerOptions.Http1Limits limits,
        Http1ReadTimeout readTimeout)
    {
        HttpHeaderCollection headers = new();

        int headerCount = 0;
        int totalBytesRemaining = limits.MaxRequestHeadersTotalSize;

        while (true)
        {
            // The per-line cap is the remaining header budget: a single line can never push the
            // header section past its total-size bound (RFC 9110 §15.5.22 → 431).
            string? line = await ReadLineAsync(
                input,
                totalBytesRemaining,
                HttpStatusCode.RequestHeaderFieldsTooLarge,
                _requestHeaderSectionSubject,
                onFirstByte: null,
                readTimeout).ConfigureAwait(false);

            if (line is null)
            {
                throw new EndOfStreamException(ErrorMessages.RequestHeadersTruncated);
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
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.RequestHeaderSectionTooLarge, limits.MaxRequestHeadersTotalSize));
            }
            totalBytesRemaining -= consumed;

            headerCount++;
            if (headerCount > limits.MaxRequestHeaderCount)
            {
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.RequestHeaderCountExceeded, limits.MaxRequestHeaderCount));
            }

            // RFC 9112 §5.1 — the field name is a token: no whitespace before its colon, never empty. A
            // server MUST answer such a request with 400, since an intermediary that reads the line
            // differently would disagree about the message (#1333). RFC 9110 §5.5 — the value, trimmed
            // of SP and HTAB only, holds no NUL, CR, LF, or other control character but HTAB (#1341).
            // The connection then closes.
            if (!Http1FieldLine.TryParse(line, out string name, out string value, out string? violation))
            {
                throw new Http1BadRequestException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.Http1HeaderSectionInvalid, violation));
            }

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
    /// Rejects a <c>Host</c> field whose value holds an octet other than a VCHAR (RFC 9112 §3.2,
    /// RFC 9110 §7.2). Optional whitespace is already trimmed, so an empty value passes, as
    /// RFC 9112 §3.2 allows for a target URI without an authority.
    /// </summary>
    /// <exception cref="Http1BadRequestException">A <c>Host</c> field line holds such an octet.</exception>
    private static void EnsureHostFieldIsVisible(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue host))
        {
            return;
        }

        foreach (string? entry in host)
        {
            int invalid = entry is null ? -1 : entry.AsSpan().IndexOfAnyExceptInRange('!', '~');
            if (invalid >= 0)
            {
                // The value is not quoted: it may hold obs-text, and the octet in hex says enough.
                throw new Http1BadRequestException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.HostFieldCharacterInvalid, (int)entry![invalid], invalid));
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
        return HttpInterimResponseRules.ExpectsContinue(headers)
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

                ReadOnlySpan<char> list = entry.AsSpan();
                foreach (Range range in list.Split(','))
                {
                    // RFC 9110 §5.6.3 — trim SP and HTAB only, as the body reader does (#1341).
                    ReadOnlySpan<char> segment = list[range].Trim(Http1FieldLine.OptionalWhitespace);

                    // Any non-"0" segment means a body is expected. A malformed value also lands here
                    // and is rejected by the body reader afterward; soliciting first is harmless.
                    if (!segment.IsEmpty && !segment.SequenceEqual("0"))
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

            ReadOnlySpan<char> list = entry.AsSpan();
            foreach (Range range in list.Split(','))
            {
                // RFC 9110 §5.6.1 / §5.6.3 — a list element loses SP and HTAB only, so
                // "close\xA0" is not the close option (#1341).
                ReadOnlySpan<char> segment = list[range].Trim(Http1FieldLine.OptionalWhitespace);
                if (segment.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a single CRLF-terminated line off the connection's pipe, capping its length so an
    /// unbounded line cannot be buffered without limit (a live memory-exhaustion vector).
    /// </summary>
    /// <remarks>
    /// Only CRLF ends the line. A bare CR or a bare LF stays in it, for the caller to reject: the
    /// request line accepts nothing but VCHAR and SP, and a field value no control character but HTAB.
    /// The line is decoded as Latin-1, one character per octet, so an octet above 0x7F reaches the
    /// caller as itself rather than as the '?' an ASCII decode substitutes (#1341).
    /// The line is found by scanning the octets the pipe has already buffered, and only the octets
    /// through its CRLF are consumed. Everything behind it (the next field line, the body, a pipelined
    /// request, a CONNECT tunnel's first octets) stays in the pipe for the next reader, so the head
    /// never over-reads. A line that has not ended yet is moved out of the pipe into a pooled buffer
    /// bounded by the cap, so a transport whose pause threshold is below the cap keeps receiving and
    /// the CRLF can arrive. The cap counts the octets already moved, and a CR moved at the end of one
    /// read still pairs with an LF at the start of the next.
    /// </remarks>
    /// <param name="input">The connection's pipe reader.</param>
    /// <param name="maxLineSize">The maximum number of payload octets the line may contain.</param>
    /// <param name="overflowStatus">The HTTP status to reject with when the cap is exceeded.</param>
    /// <param name="subject">A short description of the line for the rejection message.</param>
    /// <param name="onFirstByte">
    /// Invoked once when the first byte of the line is read; used to signal the read-timeout phase
    /// transition. May be <see langword="null"/>.
    /// </param>
    /// <param name="readTimeout">The controller bounding both buffered and pending reads.</param>
    /// <returns>The decoded line, or <see langword="null"/> on a clean end-of-stream before any byte.</returns>
    /// <exception cref="Http1LimitExceededException">Thrown when the line exceeds <paramref name="maxLineSize"/>.</exception>
    private static async ValueTask<string?> ReadLineAsync(
        PipeReader input,
        int maxLineSize,
        HttpStatusCode overflowStatus,
        string subject,
        Action? onFirstByte,
        Http1ReadTimeout readTimeout)
    {
        bool signaledFirstByte = false;

        // The octets of a line that spans reads, already consumed from the pipe. Rented on the first
        // split; most lines arrive whole and never touch it.
        byte[]? pending = null;
        int pendingLength = 0;

        try
        {
            while (true)
            {
                ReadResult result = await readTimeout.ReadAsync(input).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (!signaledFirstByte && !buffer.IsEmpty)
                {
                    signaledFirstByte = true;
                    onFirstByte?.Invoke();
                }

                // A CRLF split across reads: the CR was moved with the rest of the line.
                if (pendingLength > 0 && pending![pendingLength - 1] == (byte)'\r' && StartsWithLineFeed(buffer))
                {
                    input.AdvanceTo(buffer.GetPosition(1));
                    return Encoding.Latin1.GetString(pending, 0, pendingLength - 1);
                }

                // Every moved octet is payload now, a trailing CR included: the octet after it is not LF.
                int remaining = maxLineSize - pendingLength;
                ReadOnlySequence<byte> line = default;
                SequencePosition lineEnd = default;
                Http1LineReader.LineScan scan = remaining < 0
                    ? Http1LineReader.LineScan.TooLong
                    : Http1LineReader.ScanLine(buffer, remaining, out line, out lineEnd);

                if (scan == Http1LineReader.LineScan.Complete)
                {
                    // RFC 9110 §5.5 — obs-text (%x80-FF) is a field-value octet; Latin-1 keeps it.
                    string value;
                    if (pendingLength == 0)
                    {
                        value = line.IsSingleSegment
                            ? Encoding.Latin1.GetString(line.FirstSpan)
                            : Encoding.Latin1.GetString(line);
                    }
                    else
                    {
                        Append(ref pending, ref pendingLength, line, maxLineSize);
                        value = Encoding.Latin1.GetString(pending!, 0, pendingLength);
                    }

                    input.AdvanceTo(lineEnd);
                    return value;
                }

                if (scan == Http1LineReader.LineScan.TooLong)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    throw new Http1LimitExceededException(
                        overflowStatus,
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.HttpLineTooLarge, subject, maxLineSize));
                }

                if (result.IsCompleted)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    if (buffer.IsEmpty && pendingLength == 0)
                    {
                        return null;
                    }

                    throw new EndOfStreamException(ErrorMessages.HttpLineTruncated);
                }

                // Incomplete: the scan proved the buffered octets fit the cap (one more than it only
                // when the last is a CR), so moving them keeps the pending line within cap + 1.
                Append(ref pending, ref pendingLength, buffer, maxLineSize);
                input.AdvanceTo(buffer.End);

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException(readTimeout.Token);
                }
            }
        }
        finally
        {
            if (pending is not null)
            {
                ArrayPool<byte>.Shared.Return(pending);
            }
        }
    }

    private static bool StartsWithLineFeed(in ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);
        return reader.TryPeek(out byte first) && first == (byte)'\n';
    }

    // Appends octets to the pending line, growing the pooled buffer geometrically up to the cap
    // (plus the one trailing CR the scan allows).
    private static void Append(ref byte[]? pending, ref int pendingLength, in ReadOnlySequence<byte> octets, int maxLineSize)
    {
        if (octets.IsEmpty)
        {
            return;
        }

        int required = pendingLength + (int)octets.Length;
        if (pending is null || pending.Length < required)
        {
            int capacity = Math.Max(required, Math.Min(maxLineSize + 1, pending is null ? 256 : pending.Length * 2));
            byte[] grown = ArrayPool<byte>.Shared.Rent(capacity);
            if (pending is not null)
            {
                pending.AsSpan(0, pendingLength).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(pending);
            }

            pending = grown;
        }

        octets.CopyTo(pending.AsSpan(pendingLength));
        pendingLength = required;
    }
}
