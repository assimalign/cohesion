using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Internal;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Forwards an HTTP exchange to another server through an <see cref="HttpMessageInvoker"/> and
/// relays the response, the shared mechanism of a reverse proxy and of an absolute-form forward
/// proxy.
/// </summary>
/// <remarks>
/// <para>
/// The forwarded request carries the client's method, headers and body, minus the fields that belong
/// to the client's connection (RFC 9110 §7.6.1), plus a <c>Via</c> entry and the forwarded headers
/// <see cref="HttpForwarderOptions"/> selects. The body is streamed as it arrives. The response comes
/// back the same way: streamed when the exchange carries <see cref="IHttpResponseStreamingFeature"/>,
/// and buffered up to <see cref="HttpForwarderOptions.MaxBufferedResponseBodySize"/> otherwise.
/// </para>
/// <para>
/// An HTTP/1.1 protocol upgrade is passed through when the destination accepts it, and a WebSocket
/// over HTTP/2 or HTTP/3 (extended <c>CONNECT</c>, RFC 8441 and RFC 9220) is carried to the
/// destination as an HTTP/1.1 WebSocket upgrade. A plain <c>CONNECT</c> belongs to an
/// <see cref="HttpTunnel"/>.
/// </para>
/// <para>
/// The forwarder borrows the invoker: it never disposes it, and every exchange shares its connection
/// pool. Configure the invoker's handler for proxying: no automatic redirects, no cookie container,
/// no automatic decompression, no proxy of its own unless one is intended, and no
/// <c>ActivityHeadersPropagator</c> unless the application wants the proxy to start a new trace hop.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpForwarder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpForwarder : IHttpForwarder
{
    private const int bufferSize = 16 * 1024;

    private readonly HttpMessageInvoker _invoker;
    private readonly HttpForwardedHeaderAction _forwardedHeaderAction;
    private readonly HttpForwardedHeaderFormat _forwardedHeaderFormat;
    private readonly bool _preserveHost;
    private readonly string? _viaPseudonym;
    private readonly bool _addViaToResponses;
    private readonly TimeSpan _activityTimeout;
    private readonly Version _version;
    private readonly HttpVersionPolicy _versionPolicy;
    private readonly long _maxBufferedResponseBodySize;

    private HttpForwarder(HttpMessageInvoker invoker, HttpForwarderOptions options)
    {
        _invoker = invoker;
        _forwardedHeaderAction = options.ForwardedHeaderAction;
        _forwardedHeaderFormat = options.ForwardedHeaderFormat;
        _preserveHost = options.PreserveHost;
        _viaPseudonym = options.ViaPseudonym;
        _addViaToResponses = options.AddViaToResponses;
        _activityTimeout = options.ActivityTimeout;
        _version = options.Version;
        _versionPolicy = options.VersionPolicy;
        _maxBufferedResponseBodySize = options.MaxBufferedResponseBodySize;
    }

    private enum UpgradeMode
    {
        None,
        Http11Upgrade,
        WebSocketBridge,
    }

    /// <summary>
    /// Creates a forwarder that sends requests through <paramref name="invoker"/>.
    /// </summary>
    /// <param name="invoker">
    /// The invoker the forwarder borrows, usually an <see cref="HttpMessageInvoker"/> over a
    /// <see cref="SocketsHttpHandler"/> configured for proxying. The caller keeps ownership and
    /// disposes it after the forwarder's last exchange.
    /// </param>
    /// <param name="options">
    /// The forwarder's options, or <see langword="null"/> for the defaults. The values are copied.
    /// </param>
    /// <returns>The forwarder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="invoker"/> is <see langword="null"/>.</exception>
    public static HttpForwarder Create(HttpMessageInvoker invoker, HttpForwarderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);

        return new HttpForwarder(invoker, options ?? new HttpForwarderOptions());
    }

    /// <summary>
    /// Removes the dot segments from a decoded request path (RFC 3986 §5.2.4), so the path a proxy
    /// routes on is the path the destination receives.
    /// </summary>
    /// <param name="path">The request's decoded path.</param>
    /// <param name="normalized">
    /// When this method returns <see langword="true"/>, the path without <c>.</c> and <c>..</c>
    /// segments; an asterisk-form or empty path becomes <c>/</c>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the path hides a dot segment behind an encoded slash
    /// (<c>%2F..%2F</c>) or a backslash, which destinations interpret differently; the request should
    /// be refused with 400 Bad Request. Otherwise, <see langword="true"/>.
    /// </returns>
    public static bool TryNormalizePath(HttpPath path, out HttpPath normalized)
    {
        if (HttpProxyPath.TryNormalize(path.Value, out string value))
        {
            normalized = new HttpPath(value);
            return true;
        }

        normalized = default;
        return false;
    }

    /// <summary>
    /// Builds the complete destination URI of a forwarded request: the destination prefix, the
    /// request's path below it, and the request's query.
    /// </summary>
    /// <param name="destinationPrefix">
    /// The absolute <c>http</c> or <c>https</c> URI the request is forwarded to, which may include a
    /// base path (<c>https://backend.internal/v1</c>). A trailing <c>/</c> is ignored.
    /// </param>
    /// <param name="path">
    /// The path to append, normalized by <see cref="TryNormalizePath"/>; a reverse proxy passes the
    /// part of the request's path below its route's prefix.
    /// </param>
    /// <param name="query">
    /// The request's query. The query text the client sent is forwarded unchanged when the collection
    /// still holds it (<see cref="IHttpQueryCollection.Source"/>) and it is valid query text; otherwise
    /// the collection is encoded.
    /// </param>
    /// <returns>The destination URI, whose path and query are not canonicalized again.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destinationPrefix"/> or <paramref name="query"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationPrefix"/> is not an absolute <c>http</c> or <c>https</c> URI, or has
    /// a query, a fragment or user information; or <paramref name="path"/> is not normalized.
    /// </exception>
    public static Uri CreateDestination(Uri destinationPrefix, HttpPath path, IHttpQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(destinationPrefix);
        ArgumentNullException.ThrowIfNull(query);

        if (!IsValidDestinationPrefix(destinationPrefix))
        {
            throw new ArgumentException(ErrorMessages.DestinationPrefixInvalid, nameof(destinationPrefix));
        }

        // Normalizing turns an asterisk-form path into "/"; any other change means the caller skipped
        // TryNormalizePath.
        string pathValue = path.Value ?? "/";
        if (!HttpProxyPath.TryNormalize(pathValue, out string normalized)
            || !(pathValue == "*" || string.Equals(normalized, pathValue, StringComparison.Ordinal)))
        {
            throw new ArgumentException(ErrorMessages.PathNotNormalized, nameof(path));
        }

        string prefix = destinationPrefix.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        string encodedPath = new HttpPath(normalized).ToUriComponent();
        string queryText = GetQueryText(query);

        string text = string.Concat(
            prefix.AsSpan().TrimEnd('/'),
            encodedPath,
            queryText.Length == 0 ? string.Empty : "?",
            queryText);

        return new Uri(text, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    /// <summary>
    /// Determines whether a URI can be the destination prefix of <see cref="CreateDestination"/>.
    /// </summary>
    /// <param name="destinationPrefix">The URI to test.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="destinationPrefix"/> is an absolute <c>http</c> or
    /// <c>https</c> URI with no query, fragment or user information; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// A proxy that configures its destinations ahead of time checks each one here, so a destination
    /// that cannot be used fails the configuration instead of every request routed to it.
    /// </remarks>
    public static bool IsValidDestinationPrefix([NotNullWhen(true)] Uri? destinationPrefix)
    {
        return destinationPrefix is not null
            && IsHttpUri(destinationPrefix)
            && destinationPrefix.Query.Length == 0
            && destinationPrefix.Fragment.Length == 0
            && destinationPrefix.UserInfo.Length == 0;
    }

    /// <inheritdoc />
    public async Task<HttpProxyResult> ForwardAsync(IHttpContext context, Uri destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destination);

        if (!IsHttpUri(destination))
        {
            throw new ArgumentException(ErrorMessages.DestinationInvalid, nameof(destination));
        }

        IHttpRequest request = context.Request;

        if (_viaPseudonym is not null
            && request.Headers.TryGetValue(HttpHeaderKey.Via, out HttpHeaderValue via)
            && HttpProxyFields.ViaContains(via, _viaPseudonym))
        {
            return Answer(context, HttpStatusCode.LoopDetected, HttpProxyResult.LoopDetected);
        }

        IHttpExtendedConnectFeature? extendedConnect = context.ExtendedConnect;
        IHttpProtocolUpgrade? upgrade = context.Upgrade;
        UpgradeMode mode = UpgradeMode.None;

        if (extendedConnect is not null)
        {
            if (!string.Equals(extendedConnect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase))
            {
                return Answer(context, HttpStatusCode.NotImplemented, HttpProxyResult.NotSupported);
            }

            mode = UpgradeMode.WebSocketBridge;
        }
        else if (request.Method == HttpMethod.Connect || upgrade?.Kind == HttpProtocolUpgradeKind.Connect)
        {
            return Answer(context, HttpStatusCode.NotImplemented, HttpProxyResult.NotSupported);
        }
        else if (upgrade is { Kind: HttpProtocolUpgradeKind.Upgrade } && !string.IsNullOrEmpty(upgrade.Protocol))
        {
            mode = UpgradeMode.Http11Upgrade;
        }

        int? maxForwards = null;
        if (request.Method == HttpMethod.Options || request.Method == HttpMethod.Trace)
        {
            if (TryGetMaxForwards(request.Headers, out int value))
            {
                if (value == 0)
                {
                    // RFC 9110 §7.6.2: the proxy answers as the final recipient. It does not reflect a
                    // TRACE, which would echo credentials the client sent (cross-site tracing).
                    return Answer(
                        context,
                        request.Method == HttpMethod.Options ? HttpStatusCode.Ok : HttpStatusCode.NotImplemented,
                        HttpProxyResult.AnsweredByProxy);
                }

                maxForwards = value - 1;
            }
        }

        string authority = destination.Authority;
        using ActivityTimeout activity = new(_activityTimeout, cancellationToken, context.RequestCancelled);
        using HttpRequestMessage message = CreateRequest(context, destination, mode, maxForwards, activity, out StreamingRequestContent? content, out string? webSocketKey);

        HttpResponseMessage response;
        try
        {
            response = await _invoker.SendAsync(message, activity.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return FailRequest(context, authority, activity, content, exception);
        }

        using (response)
        {
            if (response.StatusCode == NetHttpStatusCode.SwitchingProtocols)
            {
                return mode == UpgradeMode.None
                    ? FailUpgrade(context, authority)
                    : await RelayUpgradeAsync(context, response, mode, upgrade, extendedConnect, webSocketKey, activity, authority).ConfigureAwait(false);
            }

            // A success status answers an extended CONNECT by opening the tunnel (RFC 8441 §5), so a
            // destination that ignored the upgrade cannot be relayed as if it had accepted it.
            if (mode == UpgradeMode.WebSocketBridge && response.IsSuccessStatusCode)
            {
                return FailUpgrade(context, authority);
            }

            return await RelayResponseAsync(context, response, activity, authority).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage CreateRequest(
        IHttpContext context,
        Uri destination,
        UpgradeMode mode,
        int? maxForwards,
        ActivityTimeout activity,
        out StreamingRequestContent? content,
        out string? webSocketKey)
    {
        IHttpRequest request = context.Request;
        NetHttpMethod method = mode == UpgradeMode.WebSocketBridge
            ? NetHttpMethod.Get
            : NetHttpMethod.Parse(request.Method.Value ?? HttpMethod.Get.Value);

        HttpRequestMessage message = new(method, destination);

        if (mode == UpgradeMode.None)
        {
            message.Version = _version;
            message.VersionPolicy = _versionPolicy;
        }
        else
        {
            // Only HTTP/1.1 switches protocols (RFC 9110 §7.8).
            message.Version = NetHttpVersion.Version11;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        content = mode == UpgradeMode.None && HasBody(request, context.Version)
            ? new StreamingRequestContent(request.Body, activity)
            : null;
        message.Content = content;

        IHttpHeaderCollection headers = request.Headers;
        HashSet<string>? options = headers.TryGetValue(HttpHeaderKey.Connection, out HttpHeaderValue connection)
            ? HttpProxyFields.GetConnectionOptions(connection)
            : null;
        List<string>? cookies = null;

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            string name = header.Key.Value ?? string.Empty;

            if (name.Length == 0
                || name[0] == ':'
                || HttpProxyFields.IsRequestHopByHop(name)
                || (options is not null && options.Contains(name))
                || header.Key == HttpHeaderKey.Host
                || (_viaPseudonym is not null && header.Key == HttpHeaderKey.Via)
                || (maxForwards.HasValue && header.Key == HttpHeaderKey.MaxForwards)
                || IsReplacedForwardedField(name)
                || (mode == UpgradeMode.WebSocketBridge && (header.Key == HttpHeaderKey.SecWebSocketKey || header.Key == HttpHeaderKey.SecWebSocketAccept)))
            {
                continue;
            }

            // HTTP/2 and HTTP/3 split Cookie into one field per crumb (RFC 9113 §8.2.3); HTTP/1.1
            // allows one Cookie field, so the crumbs are joined again.
            if (header.Key == HttpHeaderKey.Cookie)
            {
                cookies ??= new List<string>();
                cookies.AddRange(GetValues(header.Value));
                continue;
            }

            List<string> values = GetValues(header.Value);
            if (!message.Headers.TryAddWithoutValidation(name, values))
            {
                // A content field (Content-Type, Content-Length, ...) belongs on the content; without a
                // body there is nothing for it to describe.
                content?.Headers.TryAddWithoutValidation(name, values);
            }
        }

        if (headers.TryGetValue(HttpHeaderKey.TE, out HttpHeaderValue te) && HttpProxyFields.AcceptsTrailers(te))
        {
            message.Headers.TryAddWithoutValidation("TE", "trailers");
        }

        if (_preserveHost && !request.Host.IsEmpty)
        {
            message.Headers.TryAddWithoutValidation("Host", request.Host.Value);
        }

        if (maxForwards.HasValue)
        {
            message.Headers.TryAddWithoutValidation("Max-Forwards", maxForwards.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (cookies is { Count: > 0 })
        {
            message.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        AddForwardedHeaders(context, message);

        if (_viaPseudonym is not null)
        {
            List<string> values = headers.TryGetValue(HttpHeaderKey.Via, out HttpHeaderValue incoming)
                ? GetValues(incoming)
                : new List<string>(1);
            values.Add(HttpProxyFields.GetViaProtocol(context.Version) + " " + _viaPseudonym);
            message.Headers.TryAddWithoutValidation("Via", values);
        }

        webSocketKey = null;

        if (mode == UpgradeMode.Http11Upgrade)
        {
            message.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            message.Headers.TryAddWithoutValidation("Upgrade", context.Upgrade!.Protocol);
        }
        else if (mode == UpgradeMode.WebSocketBridge)
        {
            webSocketKey = WebSocketHandshake.CreateKey();
            message.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            message.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            message.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", webSocketKey);

            if (!headers.ContainsKey(HttpHeaderKey.SecWebSocketVersion))
            {
                message.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", WebSocketHandshake.Version);
            }
        }

        return message;
    }

    // Set replaces both families with this proxy's view of the client; Append rebuilds the configured
    // family from the client's values plus this hop and leaves the other family as the client sent it.
    private bool IsReplacedForwardedField(string name)
    {
        return _forwardedHeaderAction != HttpForwardedHeaderAction.None
            && HttpProxyFields.IsForwardedField(name)
            && (_forwardedHeaderAction == HttpForwardedHeaderAction.Set || HttpProxyFields.IsInFamily(name, _forwardedHeaderFormat));
    }

    private void AddForwardedHeaders(IHttpContext context, HttpRequestMessage message)
    {
        if (_forwardedHeaderAction == HttpForwardedHeaderAction.None)
        {
            return;
        }

        IHttpRequest request = context.Request;
        bool append = _forwardedHeaderAction == HttpForwardedHeaderAction.Append;
        IPAddress? address = append ? context.ConnectionInfo.RemoteIp : context.EffectiveRemoteIp;
        HttpScheme scheme = append ? request.Scheme : context.EffectiveScheme;
        HttpHost host = append ? request.Host : context.EffectiveHost;

        if (address is { IsIPv4MappedToIPv6: true })
        {
            address = address.MapToIPv4();
        }

        string? proto = HttpProxyFields.GetSchemeName(scheme);
        string? hostValue = host.IsEmpty ? null : host.Value;

        if (_forwardedHeaderFormat == HttpForwardedHeaderFormat.XForwarded)
        {
            AddForwardedValue(message, request, "X-Forwarded-For", address?.ToString(), append);
            AddForwardedValue(message, request, "X-Forwarded-Proto", proto, append);
            AddForwardedValue(message, request, "X-Forwarded-Host", hostValue, append);
            return;
        }

        HttpForwardedElement element = HttpForwardedElement.Create(
            @for: address is null ? HttpForwardedNode.Unknown : HttpForwardedNode.FromIPAddress(address),
            host: hostValue,
            proto: proto);
        AddForwardedValue(message, request, "Forwarded", element.Serialize(), append);
    }

    private static void AddForwardedValue(HttpRequestMessage message, IHttpRequest request, string name, string? value, bool append)
    {
        List<string> values = append && request.Headers.TryGetValue(name, out HttpHeaderValue incoming)
            ? GetValues(incoming)
            : new List<string>(1);

        if (value is not null)
        {
            values.Add(value);
        }

        if (values.Count > 0)
        {
            message.Headers.TryAddWithoutValidation(name, values);
        }
    }

    private async Task<HttpProxyResult> RelayUpgradeAsync(
        IHttpContext context,
        HttpResponseMessage response,
        UpgradeMode mode,
        IHttpProtocolUpgrade? upgrade,
        IHttpExtendedConnectFeature? extendedConnect,
        string? webSocketKey,
        ActivityTimeout activity,
        string authority)
    {
        bool verified = mode == UpgradeMode.Http11Upgrade
            ? string.Equals(GetFirstValue(response.Headers, "Upgrade"), upgrade!.Protocol, StringComparison.OrdinalIgnoreCase)
            : string.Equals(GetFirstValue(response.Headers, "Sec-WebSocket-Accept"), WebSocketHandshake.ComputeAccept(webSocketKey!), StringComparison.Ordinal);

        if (!verified)
        {
            return FailUpgrade(context, authority);
        }

        // The transport writes the 101 (or the 200 that opens an extended CONNECT) with these fields
        // and its own framing ones.
        CopyResponseHeaders(response, context.Response.Headers, isUpgrade: true, bridgesWebSocket: mode == UpgradeMode.WebSocketBridge, trailersSupported: false);

        Stream destinationStream;
        Stream clientStream;

        try
        {
            destinationStream = await response.Content.ReadAsStreamAsync(activity.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return FailRequest(context, authority, activity, null, exception);
        }

        try
        {
            clientStream = mode == UpgradeMode.Http11Upgrade
                ? await upgrade!.AcceptAsync(activity.Token).ConfigureAwait(false)
                : await extendedConnect!.AcceptAsync(activity.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await destinationStream.DisposeAsync().ConfigureAwait(false);
            context.Cancel();
            return HttpProxyResult.Canceled;
        }

        StreamRelayResult relay = await StreamRelay.RelayAsync(clientStream, destinationStream, activity).ConfigureAwait(false);

        return activity.IsCanceledByCaller && relay.Failure is OperationCanceledException
            ? HttpProxyResult.Canceled
            : HttpProxyResult.Completed;
    }

    private async Task<HttpProxyResult> RelayResponseAsync(IHttpContext context, HttpResponseMessage response, ActivityTimeout activity, string authority)
    {
        Stream body;

        try
        {
            body = await response.Content.ReadAsStreamAsync(activity.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return FailRequest(context, authority, activity, null, exception);
        }

        IHttpResponseStreamingFeature? streaming = context.Features.Get<IHttpResponseStreamingFeature>();

        if (streaming is not null)
        {
            return await StreamResponseAsync(context, response, body, streaming, activity, authority).ConfigureAwait(false);
        }

        HttpProxyEventSource.Log.ResponseBuffered();
        return await BufferResponseAsync(context, response, body, activity, authority).ConfigureAwait(false);
    }

    private async Task<HttpProxyResult> StreamResponseAsync(
        IHttpContext context,
        HttpResponseMessage response,
        Stream body,
        IHttpResponseStreamingFeature streaming,
        ActivityTimeout activity,
        string authority)
    {
        IHttpResponse clientResponse = context.Response;
        clientResponse.StatusCode = (int)response.StatusCode;
        List<HttpHeaderKey> written = CopyResponseHeaders(
            response,
            clientResponse.Headers,
            isUpgrade: false,
            bridgesWebSocket: false,
            trailersSupported: clientResponse.Trailers.IsSupported);
        AddResponseVia(response, clientResponse.Headers);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await body.ReadAsync(buffer, activity.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    return FailResponseBody(context, streaming.HasStarted, written, authority, activity, exception);
                }

                if (read == 0)
                {
                    break;
                }

                activity.Restart();

                try
                {
                    await streaming.WriteAsync(buffer.AsMemory(0, read), activity.Token).ConfigureAwait(false);
                    await streaming.FlushAsync(activity.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                    // The client stopped receiving; nothing more can reach it.
                    context.Cancel();
                    return HttpProxyResult.Canceled;
                }

                activity.Restart();
            }

            CopyTrailers(response, clientResponse);

            if (streaming.HasStarted)
            {
                await streaming.CompleteAsync(activity.Token).ConfigureAwait(false);
            }

            return HttpProxyResult.Completed;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            context.Cancel();
            return HttpProxyResult.Canceled;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpProxyResult> BufferResponseAsync(
        IHttpContext context,
        HttpResponseMessage response,
        Stream body,
        ActivityTimeout activity,
        string authority)
    {
        IHttpResponse clientResponse = context.Response;
        Stream target = clientResponse.Body;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long total = 0;

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await body.ReadAsync(buffer, activity.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    ResetBody(clientResponse);
                    return FailResponseBody(context, started: false, written: null, authority, activity, exception);
                }

                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _maxBufferedResponseBodySize)
                {
                    ResetBody(clientResponse);
                    HttpProxyEventSource.Log.ResponseTooLarge(_maxBufferedResponseBodySize, authority);
                    return Answer(context, HttpStatusCode.BadGateway, HttpProxyResult.ResponseTooLarge);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), activity.Token).ConfigureAwait(false);
                activity.Restart();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // The head is staged only once the body is complete, so a failure above leaves nothing of the
        // destination's response behind.
        clientResponse.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, clientResponse.Headers, isUpgrade: false, bridgesWebSocket: false, trailersSupported: clientResponse.Trailers.IsSupported);
        AddResponseVia(response, clientResponse.Headers);
        CopyTrailers(response, clientResponse);
        return HttpProxyResult.Completed;
    }

    private List<HttpHeaderKey> CopyResponseHeaders(
        HttpResponseMessage response,
        IHttpHeaderCollection target,
        bool isUpgrade,
        bool bridgesWebSocket,
        bool trailersSupported)
    {
        HashSet<string>? options = response.Headers.TryGetValues("Connection", out IEnumerable<string>? connection)
            ? HttpProxyFields.GetConnectionOptions(connection)
            : null;
        List<HttpHeaderKey> written = new();

        CopyFields(response.Headers.NonValidated);

        if (!isUpgrade)
        {
            CopyFields(response.Content.Headers.NonValidated);
        }

        return written;

        void CopyFields(HttpHeadersNonValidated fields)
        {
            foreach (KeyValuePair<string, HeaderStringValues> field in fields)
            {
                string name = field.Key;

                if (HttpProxyFields.IsResponseHopByHop(name)
                    || (options is not null && options.Contains(name))
                    || (!trailersSupported && string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase))
                    || (isUpgrade && (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase)))
                    || (bridgesWebSocket && string.Equals(name, "Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                HttpHeaderKey key = new(name);
                target[key] = ToHeaderValue(field.Value);
                written.Add(key);
            }
        }
    }

    private void AddResponseVia(HttpResponseMessage response, IHttpHeaderCollection target)
    {
        if (!_addViaToResponses || _viaPseudonym is null)
        {
            return;
        }

        string entry = HttpProxyFields.GetViaProtocol(response.Version) + " " + _viaPseudonym;
        target[HttpHeaderKey.Via] = target.TryGetValue(HttpHeaderKey.Via, out HttpHeaderValue existing)
            ? HttpHeaderValue.Concat(existing, entry)
            : new HttpHeaderValue(entry);
    }

    private static void CopyTrailers(HttpResponseMessage response, IHttpResponse clientResponse)
    {
        IHttpTrailerCollection trailers = clientResponse.Trailers;

        if (!trailers.IsSupported)
        {
            return;
        }

        foreach (KeyValuePair<string, HeaderStringValues> field in response.TrailingHeaders.NonValidated)
        {
            HttpHeaderKey key = new(field.Key);

            if (HttpFieldRules.IsProhibitedInTrailers(key) || HttpFieldRules.IsConnectionSpecific(key))
            {
                continue;
            }

            try
            {
                trailers[key] = ToHeaderValue(field.Value);
            }
            catch (ArgumentException)
            {
                // A trailer value the transport cannot encode is dropped; the body already reached the
                // client and the exchange still completes.
            }
        }
    }

    private HttpProxyResult FailRequest(IHttpContext context, string authority, ActivityTimeout activity, StreamingRequestContent? content, Exception exception)
    {
        if (activity.IsCanceledByCaller)
        {
            context.Cancel();
            return HttpProxyResult.Canceled;
        }

        if (content is { SourceFailed: true })
        {
            HttpProxyEventSource.Log.RequestBodyFailed(authority, exception);
            return Answer(context, HttpStatusCode.BadRequest, HttpProxyResult.RequestBodyFailed);
        }

        HttpProxyResult result;
        HttpStatusCode status;

        if (exception is OperationCanceledException)
        {
            result = HttpProxyResult.DestinationTimedOut;
            status = HttpStatusCode.GatewayTimeout;
        }
        else if (exception is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError
        })
        {
            result = HttpProxyResult.DestinationUnreachable;
            status = HttpStatusCode.BadGateway;
        }
        else
        {
            result = HttpProxyResult.DestinationFailed;
            status = HttpStatusCode.BadGateway;
        }

        HttpProxyEventSource.Log.DestinationFailed(result, authority, exception);
        return Answer(context, status, result);
    }

    private static HttpProxyResult FailResponseBody(
        IHttpContext context,
        bool started,
        List<HttpHeaderKey>? written,
        string authority,
        ActivityTimeout activity,
        Exception exception)
    {
        if (activity.IsCanceledByCaller)
        {
            context.Cancel();
            return HttpProxyResult.Canceled;
        }

        HttpProxyEventSource.Log.DestinationFailed(HttpProxyResult.ResponseBodyFailed, authority, exception);

        if (started)
        {
            // Part of the body reached the client; ending the exchange cleanly would present the
            // truncated body as complete.
            context.Cancel();
            return HttpProxyResult.ResponseBodyFailed;
        }

        if (written is not null)
        {
            foreach (HttpHeaderKey key in written)
            {
                context.Response.Headers.Remove(key);
            }
        }

        return Answer(context, HttpStatusCode.BadGateway, HttpProxyResult.ResponseBodyFailed);
    }

    private static HttpProxyResult FailUpgrade(IHttpContext context, string authority)
    {
        HttpProxyEventSource.Log.DestinationFailed(HttpProxyResult.UpgradeFailed, authority, null);
        return Answer(context, HttpStatusCode.BadGateway, HttpProxyResult.UpgradeFailed);
    }

    private static HttpProxyResult Answer(IHttpContext context, HttpStatusCode status, HttpProxyResult result)
    {
        context.Response.StatusCode = status;
        return result;
    }

    private static void ResetBody(IHttpResponse response)
    {
        if (response.Body.CanSeek)
        {
            response.Body.SetLength(0);
        }
        else
        {
            response.Body = new MemoryStream();
        }
    }

    // HTTP/1.1 frames a request body only with Content-Length or Transfer-Encoding (RFC 9112 §6.3).
    // HTTP/2 and HTTP/3 need neither, so a request without Content-Length is taken to carry a body
    // unless its method defines none.
    private static bool HasBody(IHttpRequest request, HttpVersion version)
    {
        if (request.Headers.ContainsKey(HttpHeaderKey.ContentLength) || request.Headers.ContainsKey(HttpHeaderKey.TransferEncoding))
        {
            return true;
        }

        if (version is not (HttpVersion.Http20 or HttpVersion.Http30))
        {
            return false;
        }

        HttpMethod method = request.Method;
        return method != HttpMethod.Get
            && method != HttpMethod.Head
            && method != HttpMethod.Delete
            && method != HttpMethod.Options
            && method != HttpMethod.Trace
            && method != HttpMethod.Connect;
    }

    private static bool TryGetMaxForwards(IHttpHeaderCollection headers, out int value)
    {
        value = 0;
        return headers.TryGetValue(HttpHeaderKey.MaxForwards, out HttpHeaderValue field)
            && field.Count == 1
            && int.TryParse(field.Value.AsSpan().Trim(" \t"), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string GetQueryText(IHttpQueryCollection query)
    {
        // Any implementation may supply Source, so text that is not a valid query is still refused.
        if (query.Source is { } source && IsQueryText(source.Value))
        {
            return source.Value ?? string.Empty;
        }

        return query.Count == 0 ? string.Empty : HttpQuery.Create(query).Value ?? string.Empty;
    }

    // RFC 3986 §3.4: query = *( pchar / "/" / "?" ), with '%' allowed as the start of an escape.
    private static bool IsQueryText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=:@/?%".Contains(character)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHttpUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetFirstValue(HttpResponseHeaders headers, string name)
    {
        if (!headers.NonValidated.TryGetValues(name, out HeaderStringValues values))
        {
            return null;
        }

        foreach (string value in values)
        {
            int comma = value.IndexOf(',');
            return (comma < 0 ? value : value[..comma]).Trim();
        }

        return null;
    }

    private static List<string> GetValues(HttpHeaderValue value)
    {
        List<string> values = new(Math.Max(value.Count, 1));

        foreach (string? item in value)
        {
            if (item is not null)
            {
                values.Add(item);
            }
        }

        return values;
    }

    private static HttpHeaderValue ToHeaderValue(HeaderStringValues values)
    {
        if (values.Count == 1)
        {
            foreach (string value in values)
            {
                return new HttpHeaderValue(value);
            }
        }

        string[] items = new string[values.Count];
        int index = 0;
        foreach (string value in values)
        {
            items[index++] = value;
        }

        return new HttpHeaderValue(items);
    }

    private sealed class DebugView
    {
        public DebugView(HttpForwarder value)
        {
        }
    }
}
