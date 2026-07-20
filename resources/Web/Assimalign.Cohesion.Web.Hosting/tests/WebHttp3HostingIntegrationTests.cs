using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using CohesionHttpVersion = Assimalign.Cohesion.Http.HttpVersion;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// End-to-end coverage for the HTTP/3 registration surface (issue #767): a Web application configured
/// through <c>builder.Server.UseServer(options =&gt; options.UseHttp3(...))</c> materializes a real QUIC
/// listener, a real .NET HTTP/3 client completes the QUIC handshake against it, and the Web pipeline
/// dispatches the request reporting <see cref="CohesionHttpVersion.Http30"/> and the transport-derived
/// <see cref="HttpScheme.Https"/> scheme, mirroring the <c>Http.Connections</c> HTTP/3 example.
/// </summary>
/// <remarks>
/// The suite gates on <see cref="QuicListener.IsSupported"/>: where the platform has no QUIC
/// implementation (for example a missing libmsquic) it returns early, and the deferred-factory and
/// platform-guard behaviour stays covered by <see cref="WebHttp3HostingExtensionsTests"/>.
/// <para>
/// Both halves are load-bearing assertions: the client observes the full response round-trip (HTTP/3
/// status <b>and</b> body), and the terminal middleware observes the protocol + transport-derived
/// scheme on the dispatched <see cref="IHttpContext"/>. The client round-trip became a hard assertion
/// once issue #928 — an <c>Http.Connections</c> HTTP/3 send-path defect that left the request stream
/// unterminated and surfaced at the client as <c>H3_CLOSED_CRITICAL_STREAM</c> (0x104) — was fixed;
/// the server now ends the request stream when the response completes (RFC 9114 §4.1).
/// </para>
/// </remarks>
// System.Net.Quic is Windows/Linux/macOS only; annotated to match UseHttp3 and gated at runtime.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class WebHttp3HostingIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should complete a full HTTP/3 response round-trip and dispatch reporting the https scheme")]
    public async Task UseHttp3_OverQuic_ShouldCompleteResponseRoundTripAndDispatchWithHttps()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the full composition-root path: the QUIC listener is registered inside
        // builder.Server.UseServer(...), materialized when the server resolves, then serves the request.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int port = GetAvailableUdpPort();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options =>
        {
            options.UseHttp3(quic =>
            {
                quic.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
                quic.ServerAuthenticationOptions.ServerCertificate = certificate;
            });
        });

        WebApplication app = builder.Build();

        // The terminal middleware records the protocol and transport-derived scheme it observes, then
        // answers 200 with a small body. Both the observation and the client's received body are asserted.
        TaskCompletionSource<(CohesionHttpVersion Version, HttpScheme Scheme)> observed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            observed.TrySetResult((context.Version, context.Request.Scheme));

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            context.Response.Body = new MemoryStream(Encoding.UTF8.GetBytes("hello-http3"));

            return Task.CompletedTask;
        });

        // Resolving the server materializes the QUIC listener (the deferred factory blocks once on the
        // async bind here); StartAsync then launches the accept loop.
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellationToken);

        try
        {
            // Act — a real .NET HTTP/3 client completes the full round-trip: headers AND response body.
            (Version version, int status, string body) = await GetHttp3ResponseAsync(
                new Uri($"https://127.0.0.1:{port}/secure?probe=h3"), cancellationToken);

            // Assert — the client observed a complete HTTP/3 response.
            version.ShouldBe(NetHttpVersion.Version30);
            status.ShouldBe(200);
            body.ShouldBe("hello-http3");

            // Assert — the request reached the pipeline over a real QUIC h3 connection and reported
            // HTTP/3 with the transport-derived https scheme.
            (CohesionHttpVersion observedVersion, HttpScheme observedScheme) = await observed.Task.WaitAsync(cancellationToken);

            observedVersion.ShouldBe(CohesionHttpVersion.Http30);
            observedScheme.ShouldBe(HttpScheme.Https);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Issues an exact-HTTP/3 request and reads the full response, returning the negotiated version,
    /// status code, and body. Connect/handshake races against server startup surface as a transient
    /// <see cref="HttpRequestException"/> and are retried until the shared timeout; the HTTP/3
    /// send-path defect that previously forced a best-effort read (issue #928) is fixed, so a healthy
    /// platform returns a complete response.
    /// </summary>
    private static async Task<(Version Version, int Status, string Body)> GetHttp3ResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpClientHandler handler = new()
        {
            // The certificate is a throwaway self-signed test cert; accept it unconditionally.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using HttpClient client = new(handler);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpRequestMessage request = new(ClientHttpMethod.Get, uri)
                {
                    Version = NetHttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return (response.Version, (int)response.StatusCode, body);
            }
            catch (HttpRequestException)
            {
                // Connect/handshake not ready yet — retry until the observation cancels or the timeout fires.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int GetAvailableUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
