using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

/// <summary>
/// Exercises the registration-hint + transport-probe rule for IsSecure on
/// the HTTP connection layer. The effective value visible on
/// <see cref="IHttpContext.ConnectionInfo"/> is
/// <c>registrationHint || transportContext.IsSecure</c>, so transport
/// middleware that establishes a secure session post-accept
/// (TLS over TCP via SslStream, etc.) flips the HTTP layer's view
/// without the listener registration having to know in advance.
/// </summary>
public class HttpConnectionIsSecurePropagationTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 with registration=false and transport=false should be insecure")]
    public async Task IsSecure_OnHttp1WithBothFalse_ShouldExposeFalse()
    {
        IHttpContext context = await DriveSingleHttp1RequestAsync(
            registrationIsSecure: false,
            transportIsSecure: false);

        context.ConnectionInfo.IsSecure.ShouldBeFalse();
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 with registration=true should remain secure even without transport signal")]
    public async Task IsSecure_OnHttp1WithRegistrationTrueOnly_ShouldExposeTrue()
    {
        // The registration hint stays authoritative for transports that
        // were configured up-front as secured (terminated TLS at an
        // upstream load balancer, for example).
        IHttpContext context = await DriveSingleHttp1RequestAsync(
            registrationIsSecure: true,
            transportIsSecure: false);

        context.ConnectionInfo.IsSecure.ShouldBeTrue();
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 with registration=false and transport=true should be promoted to secure")]
    public async Task IsSecure_OnHttp1WithTransportPromotion_ShouldExposeTrue()
    {
        // This is the new path: registration starts as insecure (the
        // listener doesn't know up-front), then a transport-level
        // middleware (e.g. SslStream over TCP) reports the connection
        // is secured by setting context.IsSecure = true. The HTTP layer
        // reads it post-OpenAsync and promotes the effective value.
        IHttpContext context = await DriveSingleHttp1RequestAsync(
            registrationIsSecure: false,
            transportIsSecure: true);

        context.ConnectionInfo.IsSecure.ShouldBeTrue();
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 with registration=true and transport=true should be secure")]
    public async Task IsSecure_OnHttp1WithBothTrue_ShouldExposeTrue()
    {
        IHttpContext context = await DriveSingleHttp1RequestAsync(
            registrationIsSecure: true,
            transportIsSecure: true);

        context.ConnectionInfo.IsSecure.ShouldBeTrue();
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http1 keep-alive — every request on the connection should see the same effective IsSecure")]
    public async Task IsSecure_OnHttp1KeepAliveRequests_ShouldFlowToEveryRequest()
    {
        // The probe runs once when the connection context is constructed,
        // so every subsequent request on the same connection should see
        // the same effective IsSecure.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n" +
            "GET /second HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        transportContext.IsSecure = true;
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseHttp(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }), isSecure: false);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        List<bool> isSecureFlags = new();
        await foreach (IHttpContext httpContext in httpConnectionContext.ReceiveAsync())
        {
            isSecureFlags.Add(httpContext.ConnectionInfo.IsSecure);
        }

        isSecureFlags.Count.ShouldBe(2);
        isSecureFlags.ShouldAllBe(secure => secure);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Http2 multiplexed streams should all observe the same effective IsSecure")]
    public async Task IsSecure_OnHttp2MultiplexedStreams_ShouldFlowToEveryStream()
    {
        byte[] first = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/one", "https", "api.test");
        byte[] second = HttpProtocolPayloadFactory.CreateHttp2Request(3, "GET", "/two", "https", "api.test");
        byte[] secondWithoutPreface = new byte[second.Length - 24];
        Array.Copy(second, 24, secondWithoutPreface, 0, secondWithoutPreface.Length);
        byte[] payload = new byte[first.Length + secondWithoutPreface.Length];
        Buffer.BlockCopy(first, 0, payload, 0, first.Length);
        Buffer.BlockCopy(secondWithoutPreface, 0, payload, first.Length, secondWithoutPreface.Length);

        TestTransportConnectionContext transportContext = new(payload);
        transportContext.IsSecure = true;
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseHttp(HttpProtocol.Http20, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }), isSecure: false);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        bool firstIsSecure = enumerator.Current.ConnectionInfo.IsSecure;
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        bool secondIsSecure = enumerator.Current.ConnectionInfo.IsSecure;

        firstIsSecure.ShouldBeTrue();
        secondIsSecure.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - IsSecure: Transport reporting false should not downgrade a registration of true")]
    public async Task IsSecure_OnRegistrationTrueAndTransportFalse_ShouldRemainTrue()
    {
        // Defensive: an explicitly secure registration must not be
        // demoted by a transport that simply never set the flag. The
        // effective rule is OR, not transport-overrides-registration.
        TestTransportConnectionContext transportContext = new(HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"));
        // Explicitly leave transportContext.IsSecure unset (defaults to false).
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseHttp(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }), isSecure: true);

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        IHttpContext httpContext = await ReadSingleContextAsync(httpConnectionContext);

        transportContext.IsSecure.ShouldBeFalse();
        httpContext.ConnectionInfo.IsSecure.ShouldBeTrue();
    }

    private static async Task<IHttpContext> DriveSingleHttp1RequestAsync(bool registrationIsSecure, bool transportIsSecure)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n");
        TestTransportConnectionContext transportContext = new(payload);
        transportContext.IsSecure = transportIsSecure;
        TestSingleStreamTransportConnection connection = new(transportContext, TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseHttp(HttpProtocol.Http11, new TestServerTransport(TransportProtocol.Tcp, new TransportConnection[] { connection }), registrationIsSecure);

        // The listener is intentionally returned along with the context so
        // it stays alive for the duration of the test method — disposing
        // it would also dispose the listener while we're still reading
        // assertions off the context.
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
