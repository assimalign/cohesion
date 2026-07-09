using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.ProtocolUpgrade.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ProtocolUpgrade.Tests;

public class HttpProtocolUpgradeInterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: An h1 upgrade signal installs the feature with Kind=Upgrade and the protocol")]
    public void Interceptors_OnHttp11UpgradeSignal_ShouldInstallUpgradeFeature()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();

        // Act
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream()));

        // Assert
        IHttpProtocolUpgrade? upgrade = context.Upgrade;
        upgrade.ShouldNotBeNull();
        upgrade!.Kind.ShouldBe(HttpProtocolUpgradeKind.Upgrade);
        upgrade.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: A CONNECT request installs the feature with Kind=Connect and no protocol")]
    public void Interceptors_OnConnect_ShouldInstallConnectFeature()
    {
        // Arrange
        FakeHttpContext context = new();

        // Act
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Connect, new HttpHeaderCollection(), new FakeExchangeControl(new MemoryStream()));

        // Assert
        IHttpProtocolUpgrade? upgrade = context.Upgrade;
        upgrade.ShouldNotBeNull();
        upgrade!.Kind.ShouldBe(HttpProtocolUpgradeKind.Connect);
        upgrade.Protocol.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: A bare Upgrade header without the Connection token is not a transition")]
    public void Interceptors_OnUpgradeHeaderWithoutConnectionToken_ShouldNotInstallFeature()
    {
        // RFC 9110 §7.8 — the Upgrade header is only actionable when Connection lists "upgrade".
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();

        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream()));

        context.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: The upgrade token is found inside a Connection token list")]
    public void Interceptors_OnConnectionTokenList_ShouldDetectUpgradeToken()
    {
        // "Connection: keep-alive, Upgrade" — token scanning must be comma-list aware and
        // case-insensitive.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "keep-alive, Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();

        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream()));

        context.Upgrade.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: HTTP/2 exchanges are never transitions, even with upgrade-shaped headers")]
    public void Interceptors_OnHttp2_ShouldNotInstallFeature()
    {
        // RFC 9113 §8.6 removed the Upgrade mechanism, and an HTTP/2 CONNECT (including extended
        // CONNECT) is per-stream semantics over a shared connection — never a whole-connection
        // takeover.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";

        FakeHttpContext upgradeShaped = new();
        RunInterceptors(upgradeShaped, HttpVersion.Http20, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream()));
        upgradeShaped.Upgrade.ShouldBeNull();

        FakeHttpContext connectShaped = new();
        RunInterceptors(connectShaped, HttpVersion.Http20, HttpMethod.Connect, new HttpHeaderCollection(), new FakeExchangeControl(new MemoryStream()));
        connectShaped.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: No transport takeover capability degrades to a null upgrade")]
    public void Interceptors_OnMissingTakeoverCapability_ShouldNotInstallFeature()
    {
        // A hand-built context (or a transport without exchange control) offers no Control; the
        // exchange degrades to "no upgrade available" instead of surfacing a feature whose
        // accept could never work.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();

        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, control: null);

        context.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Interceptor: A control that cannot take over degrades to a null upgrade")]
    public void Interceptors_OnControlWithoutTakeover_ShouldNotInstallFeature()
    {
        // A control whose CanTakeOver is false (an HTTP/2 / HTTP/3 multiplexed exchange) must
        // degrade to "no upgrade available" — the production gate is
        // `context.Control is { CanTakeOver: true }`, not mere control presence.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();

        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream(), canTakeOver: false));

        context.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Upgrade: No installed feature surfaces a null upgrade and never throws")]
    public void Upgrade_OnNoFeature_ShouldReturnNull()
    {
        // Regression guard: this accessor used to throw NotImplementedException on every call.
        FakeHttpContext context = new();

        context.Upgrade.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Upgrade: A null context throws")]
    public void Upgrade_OnNullContext_ShouldThrowArgumentNullException()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => { _ = context.Upgrade; });
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Accept: Upgrade claims the connection, writes 101 without framing headers, and returns the raw stream")]
    public async Task AcceptAsync_OnUpgrade_ShouldWrite101AndReturnRawStream()
    {
        // Arrange
        MemoryStream wire = new();
        FakeExchangeControl takeover = new(wire);
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, takeover);

        // Act
        Stream surrendered = await context.Upgrade!.AcceptAsync();

        // Assert
        surrendered.ShouldBeSameAs(wire);
        takeover.TakenOver.ShouldBeTrue();

        string response = Encoding.ASCII.GetString(wire.ToArray());
        response.ShouldContain("HTTP/1.1 101 Switching Protocols");
        response.ShouldContain("Connection: Upgrade");
        response.ShouldContain("Upgrade: websocket");
        // RFC 9112 §9.9 — a 101 carries no body framing.
        response.ShouldNotContain("Content-Length");
        response.ShouldNotContain("Transfer-Encoding");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Accept: CONNECT writes 200 without framing or Connection headers")]
    public async Task AcceptAsync_OnConnect_ShouldWrite200WithoutFramingHeaders()
    {
        // Arrange
        MemoryStream wire = new();
        FakeExchangeControl takeover = new(wire);
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Connect, new HttpHeaderCollection(), takeover, out HttpHeaderCollection responseHeaders);

        // A stale framing header set before accepting must be scrubbed, not emitted.
        responseHeaders[HttpHeaderKey.ContentLength] = "42";

        // Act
        await context.Upgrade!.AcceptAsync();

        // Assert
        takeover.TakenOver.ShouldBeTrue();

        string response = Encoding.ASCII.GetString(wire.ToArray());
        response.ShouldContain("HTTP/1.1 200 Ok");
        // RFC 9110 §9.3.6 — a successful CONNECT response carries no framing headers and must
        // not advertise Connection: close (the tunnel persists).
        response.ShouldNotContain("Content-Length");
        response.ShouldNotContain("Transfer-Encoding");
        response.ShouldNotContain("Connection:");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Accept: Application response headers set before accepting ride the 101")]
    public async Task AcceptAsync_OnUpgrade_ShouldEmitApplicationHeaders()
    {
        // The WebSocket handshake shape: the handler computes Sec-WebSocket-Accept before
        // accepting, and the value must ride the 101.
        MemoryStream wire = new();
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(wire), out HttpHeaderCollection responseHeaders);

        responseHeaders[new HttpHeaderKey("Sec-WebSocket-Accept")] = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

        await context.Upgrade!.AcceptAsync();

        string response = Encoding.ASCII.GetString(wire.ToArray());
        response.ShouldContain("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Accept: A second accept throws without writing a second response")]
    public async Task AcceptAsync_OnSecondCall_ShouldThrowWithoutWriting()
    {
        // Arrange
        MemoryStream wire = new();
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(wire));
        IHttpProtocolUpgrade upgrade = context.Upgrade!;

        await upgrade.AcceptAsync();
        long lengthAfterFirstAccept = wire.Length;

        // Act / Assert — the single-shot guard throws before any byte is written.
        await Should.ThrowAsync<InvalidOperationException>(async () => await upgrade.AcceptAsync());
        wire.Length.ShouldBe(lengthAfterFirstAccept);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ProtocolUpgrade] - Upgrade: Repeated accessor reads return the same single-shot instance")]
    public void Upgrade_OnRepeatedAccess_ShouldReturnSameInstance()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Connection] = "Upgrade";
        headers[HttpHeaderKey.Upgrade] = "websocket";
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http11, HttpMethod.Get, headers, new FakeExchangeControl(new MemoryStream()));

        context.Upgrade.ShouldBeSameAs(context.Upgrade);
    }

    /// <summary>
    /// Drives the interceptor pair the way a transport does: the request hook over a parse-time
    /// head context, then the response hook over a response-setup context sharing the same
    /// feature collection.
    /// </summary>
    private static void RunInterceptors(
        FakeHttpContext context,
        HttpVersion version,
        HttpMethod method,
        HttpHeaderCollection requestHeaders,
        IHttpExchangeControl? control)
        => RunInterceptors(context, version, method, requestHeaders, control, out _);

    private static void RunInterceptors(
        FakeHttpContext context,
        HttpVersion version,
        HttpMethod method,
        HttpHeaderCollection requestHeaders,
        IHttpExchangeControl? control,
        out HttpHeaderCollection responseHeaders)
    {
        HttpExchangeInterceptorRequestContext headContext = new()
        {
            Version = version,
            Method = method,
            Path = new HttpPath("/chat"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = requestHeaders.AsReadOnly(),
            Features = context.Features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };
        IHttpExchangeInterceptor interceptor = HttpProtocolUpgrade.CreateInterceptor();
        interceptor.AfterRequestHead(headContext);

        responseHeaders = new HttpHeaderCollection();
        HttpExchangeInterceptorResponseContext responseContext = new()
        {
            Version = version,
            Headers = responseHeaders,
            Features = context.Features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            ResponseBody = Stream.Null,
            Control = control,
        };
        interceptor.BeforeResponse(responseContext);
    }
}
