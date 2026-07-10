using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpAltServiceAdvertisementTests
{
    // TestMultiplexedConnectionListener binds its endpoint to loopback:16000.
    private const int Http3Port = 16000;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should advertise the h3 endpoint on an HTTP/1.1 response")]
    public async Task Http1_OnAdvertisementEnabledWithHttp3_ShouldEmitAltSvcHeader()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(advertisement => advertisement.MaxAge = TimeSpan.FromHours(24));

        // Act
        string responseText = await RunHttp1ExchangeAsync(connection, options);

        // Assert
        responseText.ShouldContain($"Alt-Svc: h3=\":{Http3Port}\"; ma=86400", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should not advertise when no HTTP/3 listener is registered")]
    public async Task Http1_OnNoHttp3Listener_ShouldNotEmitAltSvcHeader()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.AdvertiseAltService(_ => { });

        // Act
        string responseText = await RunHttp1ExchangeAsync(connection, options);

        // Assert
        responseText.ShouldNotContain("Alt-Svc");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should not advertise when advertisement is disabled")]
    public async Task Http1_OnAdvertisementDisabled_ShouldNotEmitAltSvcHeader()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        // Advertisement left disabled (the default).

        // Act
        string responseText = await RunHttp1ExchangeAsync(connection, options);

        // Assert
        responseText.ShouldNotContain("Alt-Svc");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should not overwrite an application-set Alt-Svc on HTTP/1.1")]
    public async Task Http1_OnApplicationSetAltSvc_ShouldNotOverwrite()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(_ => { });

        // Act
        string responseText = await RunHttp1ExchangeAsync(
            connection,
            options,
            httpContext => httpContext.Response.Headers[HttpHeaderKey.AltSvc] = "h3=\":9999\"; ma=1");

        // Assert — the application value is emitted and the server default (:16000) is not.
        responseText.ShouldContain("Alt-Svc: h3=\":9999\"; ma=1", Case.Sensitive);
        responseText.ShouldNotContain($":{Http3Port}");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should use an explicitly configured authority on HTTP/1.1")]
    public async Task Http1_OnExplicitAuthority_ShouldEmitConfiguredAuthority()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(advertisement =>
        {
            advertisement.Authority = "alt.example.com:8443";
            advertisement.MaxAge = TimeSpan.FromHours(1);
        });

        // Act
        string responseText = await RunHttp1ExchangeAsync(connection, options);

        // Assert
        responseText.ShouldContain("Alt-Svc: h3=\"alt.example.com:8443\"; ma=3600", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should advertise the h3 endpoint on an HTTP/2 response")]
    public async Task Http2_OnAdvertisementEnabledWithHttp3_ShouldEmitAltSvcHeader()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(advertisement => advertisement.MaxAge = TimeSpan.FromHours(24));

        // Act
        Dictionary<string, string> headers = await RunHttp2ExchangeAsync(connection, options);

        // Assert
        headers.ShouldContainKey("alt-svc");
        headers["alt-svc"].ShouldBe($"h3=\":{Http3Port}\"; ma=86400");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should not advertise on HTTP/2 when no HTTP/3 listener is registered")]
    public async Task Http2_OnNoHttp3Listener_ShouldNotEmitAltSvcHeader()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.AdvertiseAltService(_ => { });

        // Act
        Dictionary<string, string> headers = await RunHttp2ExchangeAsync(connection, options);

        // Assert
        headers.ShouldNotContainKey("alt-svc");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Should not overwrite an application-set Alt-Svc on HTTP/2")]
    public async Task Http2_OnApplicationSetAltSvc_ShouldNotOverwrite()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(_ => { });

        // Act
        Dictionary<string, string> headers = await RunHttp2ExchangeAsync(
            connection,
            options,
            httpContext => httpContext.Response.Headers[HttpHeaderKey.AltSvc] = "h3=\":9999\"; ma=1");

        // Assert
        headers.ShouldContainKey("alt-svc");
        headers["alt-svc"].ShouldBe("h3=\":9999\"; ma=1");
    }

    private static async Task<string> RunHttp1ExchangeAsync(
        TestConnection connection,
        HttpConnectionListenerOptions options,
        Action<IHttpContext>? configureResponse = null)
    {
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Response.StatusCode = HttpStatusCode.Ok;
        configureResponse?.Invoke(httpContext);
        await httpConnectionContext.SendAsync(httpContext);

        return Encoding.ASCII.GetString(await connection.ReadOutputAsync());
    }

    private static async Task<Dictionary<string, string>> RunHttp2ExchangeAsync(
        TestConnection connection,
        HttpConnectionListenerOptions options,
        Action<IHttpContext>? configureResponse = null)
    {
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        httpContext.Response.StatusCode = HttpStatusCode.Ok;
        configureResponse?.Invoke(httpContext);
        await httpConnectionContext.SendAsync(httpContext);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.ReadOutputAsync());

        foreach ((long FrameType, byte[] Payload) frame in frames)
        {
            if (frame.FrameType == 1) // HEADERS
            {
                return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frame.Payload);
            }
        }

        throw new InvalidOperationException("The HTTP/2 response did not contain a HEADERS frame.");
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
