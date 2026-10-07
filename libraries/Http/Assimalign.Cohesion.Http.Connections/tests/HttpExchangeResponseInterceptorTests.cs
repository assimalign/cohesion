using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Response interceptors added to one exchange by a request-parse hook
/// (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>): the transport runs
/// the response phase, with its response sink and exchange control, only for the exchanges that
/// asked, on every protocol. The protocol-upgrade interceptor the Web host registers by default is
/// the motivating case: a request-scoped interceptor that joins only the HTTP/1.1 exchanges asking
/// for a transition, so an ordinary exchange keeps the fast path (no sink, no control).
/// </summary>
public class HttpExchangeResponseInterceptorTests
{
    public enum Protocol
    {
        Http1,
        Http2,
        Http3,
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: An interceptor a request hook added runs every response hook for that exchange")]
    [InlineData(Protocol.Http1)]
    [InlineData(Protocol.Http2)]
    [InlineData(Protocol.Http3)]
    public async Task AddedInterceptor_OnClaimedExchange_ShouldRunTheResponsePhase(Protocol protocol)
    {
        // Arrange
        ResponseProbe probe = new(HttpInterceptorScopes.None);
        ClaimingInterceptor claimer = new(probe);
        await using Exchange exchange = await Exchange.OpenAsync(protocol, claim: true, claimer);

        // Act
        exchange.Context.Response.StatusCode = HttpStatusCode.Ok;
        await exchange.ConnectionContext.SendAsync(exchange.Context);

        // Assert — the sink and the control exist for this exchange, and each hook ran once.
        exchange.Transport.ResponseBodySink.ShouldNotBeNull();
        probe.Invocations.ShouldBe(["before-response", "before-head", "after-response"]);
        probe.CapturedControl.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: An exchange no hook claimed keeps the fast path: no response sink, no control, no response hooks")]
    [InlineData(Protocol.Http1)]
    [InlineData(Protocol.Http2)]
    [InlineData(Protocol.Http3)]
    public async Task AddedInterceptor_OnUnclaimedExchange_ShouldKeepTheFastPath(Protocol protocol)
    {
        // Arrange
        ResponseProbe probe = new(HttpInterceptorScopes.None);
        ClaimingInterceptor claimer = new(probe);
        await using Exchange exchange = await Exchange.OpenAsync(protocol, claim: false, claimer);

        // Act
        exchange.Context.Response.StatusCode = HttpStatusCode.Ok;
        await exchange.ConnectionContext.SendAsync(exchange.Context);

        // Assert
        exchange.Transport.ResponseBodySink.ShouldBeNull();
        probe.Invocations.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: Added interceptors run after the registered ones, each at most once")]
    public async Task AddedInterceptor_WithRegisteredResponseInterceptor_ShouldRunAfterItOnce()
    {
        // Arrange — the hook adds a fresh probe, then the registered one again, which must not run twice.
        List<string> order = new();
        ResponseProbe registered = new(HttpInterceptorScopes.Response, "registered", order);
        ResponseProbe added = new(HttpInterceptorScopes.None, "added", order);
        ClaimingInterceptor claimer = new(added, registered);
        await using Exchange exchange = await Exchange.OpenAsync(Protocol.Http1, claim: true, claimer, registered);

        // Act
        await exchange.ConnectionContext.SendAsync(exchange.Context);

        // Assert
        order.ShouldBe([
            "registered:before-response", "added:before-response",
            "registered:before-head", "added:before-head",
            "registered:after-response", "added:after-response",
        ]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: The protocol-upgrade interceptor costs an ordinary request no response sink or control")]
    [InlineData(Protocol.Http1)]
    [InlineData(Protocol.Http2)]
    [InlineData(Protocol.Http3)]
    public async Task ProtocolUpgradeInterceptor_OnOrdinaryRequest_ShouldKeepTheFastPath(Protocol protocol)
    {
        // Arrange — the interceptor the Web host registers on every listener by default.
        await using Exchange exchange = await Exchange.OpenAsync(protocol, claim: false, HttpProtocolUpgrade.CreateInterceptor());

        // Act
        exchange.Context.Response.StatusCode = HttpStatusCode.Ok;
        await exchange.ConnectionContext.SendAsync(exchange.Context);

        // Assert
        exchange.Transport.ResponseBodySink.ShouldBeNull();
        exchange.Context.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: An HTTP/2 extended CONNECT (a WebSocket) keeps the fast path under the protocol-upgrade interceptor")]
    public async Task ProtocolUpgradeInterceptor_OnHttp2ExtendedConnect_ShouldKeepTheFastPath()
    {
        // Arrange — HTTP/2 has no Upgrade mechanism (RFC 9113 §8.6); its WebSocket is an extended
        // CONNECT (RFC 8441), which the transport surfaces itself.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);

        // Act
        await peer.SendHeadersAsync(1, endStream: true, ExtendedConnectFields());
        IHttpContext context = await peer.ReceiveContextAsync();

        // Assert
        ((TransportHttpContext)context).ResponseBodySink.ShouldBeNull();
        context.Upgrade.ShouldBeNull();
        context.Features.Get<IHttpExtendedConnectFeature>().ShouldNotBeNull();

        await peer.ConnectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: An HTTP/3 extended CONNECT (a WebSocket) keeps the fast path under the protocol-upgrade interceptor")]
    public async Task ProtocolUpgradeInterceptor_OnHttp3ExtendedConnect_ShouldKeepTheFastPath()
    {
        // Arrange — RFC 9114 §4.2 has no Upgrade either; RFC 9220 is the WebSocket bootstrap.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: static options => options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor()));
        Connection request = await peer.OpenRequestStreamAsync();

        // Act
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(ExtendedConnectFields()));
        IHttpContext context = await peer.NextContextAsync();

        // Assert
        ((TransportHttpContext)context).ResponseBodySink.ShouldBeNull();
        context.Upgrade.ShouldBeNull();
        context.Features.Get<IHttpExtendedConnectFeature>().ShouldNotBeNull();

        await peer.ConnectionContext.SendAsync(context);
        await context.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response interceptors: The protocol-upgrade interceptor joins an HTTP/1.1 upgrade's exchange, which still switches protocols")]
    public async Task ProtocolUpgradeInterceptor_OnHttp1Upgrade_ShouldJoinTheExchangeAndSwitch()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /chat HTTP/1.1\r\nHost: api.test\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext context = await ReadSingleContextAsync(connectionContext);

        // Act
        IHttpProtocolUpgrade upgrade = context.Upgrade.ShouldNotBeNull();
        Stream tunnel = await upgrade.AcceptAsync();
        string head = Encoding.ASCII.GetString(await connection.ReadOutputAsync());

        // Assert — the response phase ran for this exchange only, and the takeover wrote the 101.
        ((TransportHttpContext)context).ResponseBodySink.ShouldNotBeNull();
        head.ShouldStartWith("HTTP/1.1 101 Switching Protocols\r\n");
        tunnel.CanWrite.ShouldBeTrue();

        await context.DisposeAsync();
    }

    private static (string Name, string Value)[] ExtendedConnectFields() =>
    [
        (":method", "CONNECT"),
        (":protocol", "websocket"),
        (":scheme", "https"),
        (":path", "/chat"),
        (":authority", "api.test"),
        ("sec-websocket-version", "13"),
    ];

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    /// <summary>
    /// One request on a listener of the given protocol, read up to its dispatch. A claimed request
    /// carries <c>Connection: Upgrade</c> and <c>Upgrade: websocket</c> on HTTP/1.1, and an
    /// <c>x-claim</c> field on every protocol.
    /// </summary>
    private sealed class Exchange : System.IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;

        private Exchange(HttpConnectionListener listener, IHttpConnectionContext connectionContext, IHttpContext context)
        {
            _listener = listener;
            ConnectionContext = connectionContext;
            Context = context;
        }

        public IHttpConnectionContext ConnectionContext { get; }

        public IHttpContext Context { get; }

        public TransportHttpContext Transport => (TransportHttpContext)Context;

        public static async Task<Exchange> OpenAsync(Protocol protocol, bool claim, params IHttpExchangeInterceptor[] interceptors)
        {
            HttpConnectionListenerOptions options = new();
            Dictionary<string, string> headers = new();

            if (claim)
            {
                headers["x-claim"] = "1";
                headers["connection"] = "Upgrade";
                headers["upgrade"] = "websocket";
            }

            switch (protocol)
            {
                case Protocol.Http1:
                    StringBuilder request = new("GET /chat HTTP/1.1\r\nHost: api.test\r\n");
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                    }

                    options.UseHttp1(new TestConnectionListener(new TestConnection(
                        HttpProtocolPayloadFactory.CreateHttp1Request(request.Append("\r\n").ToString()))));
                    break;

                case Protocol.Http2:
                    // Connection-specific fields are malformed on HTTP/2 (RFC 9113 §8.2.2); the claim is
                    // the x-claim field alone.
                    headers.Remove("connection");
                    headers.Remove("upgrade");
                    options.UseHttp2(new TestConnectionListener(new TestConnection(
                        HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/chat", "https", "api.test", headers))));
                    break;

                default:
                    headers.Remove("connection");
                    headers.Remove("upgrade");
                    options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(new TestConnection(
                        HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/chat", "https", "api.test", headers)))));
                    break;
            }

            foreach (IHttpExchangeInterceptor interceptor in interceptors)
            {
                options.Interceptors.Add(interceptor);
            }

            HttpConnectionListener listener = new(options);
            IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IHttpContext context = await ReadSingleContextAsync(connectionContext);

            return new Exchange(listener, connectionContext, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _listener.DisposeAsync();
        }
    }

    /// <summary>
    /// A request-scoped interceptor that adds its probes to the response phase of each request carrying
    /// <c>x-claim</c>, and of no other.
    /// </summary>
    private sealed class ClaimingInterceptor : HttpExchangeInterceptor
    {
        private readonly IHttpExchangeInterceptor[] _probes;

        public ClaimingInterceptor(params IHttpExchangeInterceptor[] probes) => _probes = probes;

        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
        {
            if (!context.Headers.ContainsKey(new HttpHeaderKey("x-claim")))
            {
                return;
            }

            foreach (IHttpExchangeInterceptor probe in _probes)
            {
                context.AddResponseInterceptor(probe);
            }
        }
    }

    /// <summary>
    /// Records the response hooks it receives, under a configurable declared scope.
    /// </summary>
    private sealed class ResponseProbe : HttpExchangeInterceptor
    {
        private readonly HttpInterceptorScopes _scopes;
        private readonly string? _name;

        public ResponseProbe(HttpInterceptorScopes scopes, string? name = null, List<string>? log = null)
        {
            _scopes = scopes;
            _name = name;
            Invocations = log ?? new List<string>();
        }

        public override HttpInterceptorScopes Scopes => _scopes;

        public List<string> Invocations { get; }

        public IHttpExchangeControl? CapturedControl { get; private set; }

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            CapturedControl = context.Control;
            Record("before-response");
        }

        public override ValueTask BeforeResponseHeadAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            Record("before-head");
            return ValueTask.CompletedTask;
        }

        public override ValueTask AfterResponseAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            Record("after-response");
            return ValueTask.CompletedTask;
        }

        private void Record(string hook) => Invocations.Add(_name is null ? hook : $"{_name}:{hook}");
    }
}
