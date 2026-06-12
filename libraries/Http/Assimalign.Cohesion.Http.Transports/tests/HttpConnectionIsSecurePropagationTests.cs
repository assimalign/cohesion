using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

/// <summary>
/// Exercises the capability-derived security rule on the HTTP connection
/// layer. TLS is composed onto the connection listener before registration
/// (for example via the security library's <c>UseTls</c> layer), and the
/// layered listener reports <see cref="ConnectionSecurity.Tls"/> on its
/// <see cref="IConnectionListener.Capabilities"/>; the HTTP layer derives
/// its effective scheme from that capability rather than from a
/// registration-time hint.
/// </summary>
public class HttpConnectionIsSecurePropagationTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 over a listener without transport security should expose the http scheme")]
    public async Task IsSecure_OnHttp1WithoutTlsCapability_ShouldExposeHttpScheme()
    {
        // Arrange + Act
        IHttpContext context = await DriveSingleHttp1RequestAsync(ConnectionSecurity.None);

        // Assert
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 over a TLS-capability listener should expose the https scheme")]
    public async Task IsSecure_OnHttp1WithTlsCapability_ShouldExposeHttpsScheme()
    {
        // Arrange + Act — the listener (e.g. tcp.UseTls(...)) reports
        // Security = Tls; every accepted connection is secured.
        IHttpContext context = await DriveSingleHttp1RequestAsync(ConnectionSecurity.Tls);

        // Assert
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 keep-alive — every request on a TLS-capability connection should observe https")]
    public async Task IsSecure_OnHttp1KeepAliveRequestsOverTls_ShouldFlowToEveryRequest()
    {
        // Arrange — the security capability is evaluated once per listener,
        // so every request on the same connection sees the same scheme.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n" +
            "GET /second HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestConnection connection = new(payload);
        TestConnectionListener transportListener = new(
            TestConnection.DefaultCapabilities with { Security = ConnectionSecurity.Tls },
            connection);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(transportListener);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        List<HttpScheme> schemes = new();
        await foreach (IHttpContext httpContext in httpConnectionContext.ReceiveAsync())
        {
            schemes.Add(httpContext.Request.Scheme);
        }

        // Assert
        schemes.Count.ShouldBe(2);
        schemes.ShouldAllBe(scheme => scheme == HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http2 streams over a TLS-capability listener should observe https")]
    public async Task IsSecure_OnHttp2StreamsOverTls_ShouldExposeHttpsScheme()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test");
        TestConnection connection = new(payload);
        TestConnectionListener transportListener = new(
            TestConnection.DefaultCapabilities with { Security = ConnectionSecurity.Tls },
            connection);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(transportListener);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        // Assert
        httpContext.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    private static async Task<IHttpContext> DriveSingleHttp1RequestAsync(ConnectionSecurity security)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestConnection connection = new(payload);
        TestConnectionListener transportListener = new(
            TestConnection.DefaultCapabilities with { Security = security },
            connection);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(transportListener);

        // The listener is intentionally not disposed here so it stays alive
        // for the duration of the test method — disposing it would tear the
        // accepted connection's backing state down while assertions still
        // read off the context.
        HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadSingleContextAsync(httpConnectionContext);
    }

    private static async Task<IHttpContext> ReadSingleContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
