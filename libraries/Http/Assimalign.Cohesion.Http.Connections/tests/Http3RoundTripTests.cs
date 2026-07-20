using System;
using System.IO;
using System.Net.Http;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Real-QUIC round-trip coverage for the HTTP/3 transport: a real .NET HTTP/3 client drives the
/// Cohesion h3 engine over a loopback QUIC connection and observes the full response. These pin the
/// fix for issue #928 — the server now ends the request stream when a response completes (RFC 9114
/// §4.1), so the client's response-content read finishes instead of tripping
/// <c>H3_CLOSED_CRITICAL_STREAM</c> (0x104) when the connection is later torn down.
/// </summary>
/// <remarks>
/// System.Net.Quic is Windows/Linux/macOS only, so the class is platform-annotated and every test
/// returns early when <see cref="QuicListener.IsSupported"/> is <see langword="false"/> (no libmsquic),
/// matching the gating idiom used by the existing QUIC and Web HTTP/3 suites.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class Http3RoundTripTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should complete a full request/response round-trip with status and body over real QUIC")]
    public async Task Http3_OnRequest_ShouldCompleteFullRoundTripWithStatusAndBody()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — a loopback h3 server that answers 200 with a small text body.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            exchange.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            exchange.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("hello-http3"));
            return Task.CompletedTask;
        }, cancellationToken);

        // Act — a real .NET HTTP/3 client reads the full response (headers AND content).
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await GetExactHttp3Async(client, new Uri(server.BaseUri, "/hello"), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Assert — the round-trip completed: negotiated 3.0, status 200, body intact.
        response.Version.ShouldBe(NetHttpVersion.Version30);
        ((int)response.StatusCode).ShouldBe(200);
        body.ShouldBe("hello-http3");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should complete a bodyless 200 round-trip without hanging the client drain")]
    public async Task Http3_OnBodylessResponse_ShouldCompleteRoundTripWithoutHanging()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — a 200 with no body (Content-Length: 0, no DATA frame). Before the fix the client's
        // content-length-0 drain never completed because the request stream was never ended.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        }, cancellationToken);

        // Act
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await GetExactHttp3Async(client, new Uri(server.BaseUri, "/empty"), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Assert — the client observed the (empty) body terminate and returned cleanly.
        response.Version.ShouldBe(NetHttpVersion.Version30);
        ((int)response.StatusCode).ShouldBe(200);
        body.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should return the full body when the response body stream is left at its end position")]
    public async Task Http3_OnBufferedBodyWrittenToStreamEnd_ShouldReturnFullBody()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the handler writes the body through Response.Body, leaving the default MemoryStream
        // positioned at its end. The send path must emit the whole buffer (Content-Length AND DATA
        // consistent), independent of the stream position — the body-position observation on #928.
        const string payload = "written-to-end-position";
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(async exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await exchange.Response.Body.WriteAsync(bytes, cancellationToken);
        }, cancellationToken);

        // Act
        using HttpClient client = CreateHttp3Client();
        using HttpResponseMessage response = await GetExactHttp3Async(client, new Uri(server.BaseUri, "/buffered"), cancellationToken);
        byte[] received = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Assert — the full body arrived; Content-Length matched the DATA the client read.
        ((int)response.StatusCode).ShouldBe(200);
        response.Content.Headers.ContentLength.ShouldBe(payload.Length);
        Encoding.UTF8.GetString(received).ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Should serve sequential requests on one connection, keeping the control/QPACK critical streams open")]
    public async Task Http3_OnSequentialRequests_ShouldReuseConnectionAndKeepCriticalStreamsOpen()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — echo the request path in the body so each response is distinguishable. A real .NET
        // HTTP/3 client reuses one QUIC connection across sequential requests; each request completing
        // proves the server's control and QPACK streams stayed open for the connection lifetime (a
        // closed critical stream is exactly the 0x104 error, which would fail the second request).
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            exchange.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes(exchange.Request.Path.Value));
            return Task.CompletedTask;
        }, cancellationToken);

        // Act / Assert — three requests over the reused connection all complete with their own body.
        using HttpClient client = CreateHttp3Client();

        foreach (string path in new[] { "/first", "/second", "/third" })
        {
            using HttpResponseMessage response = await GetExactHttp3Async(client, new Uri(server.BaseUri, path), cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            response.Version.ShouldBe(NetHttpVersion.Version30);
            ((int)response.StatusCode).ShouldBe(200);
            body.ShouldBe(path);
        }
    }

    private static HttpClient CreateHttp3Client()
    {
        HttpClientHandler handler = new()
        {
            // The loopback certificate is a throwaway self-signed test cert; accept it unconditionally.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler, disposeHandler: true);
    }

    private static async Task<HttpResponseMessage> GetExactHttp3Async(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(ClientHttpMethod.Get, uri)
        {
            Version = NetHttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        return await client.SendAsync(request, cancellationToken);
    }
}
