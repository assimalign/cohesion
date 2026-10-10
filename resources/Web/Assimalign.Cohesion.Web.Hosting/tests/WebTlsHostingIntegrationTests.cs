using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// End-to-end coverage for the TLS convenience surface (issue #763): a listener registered through
/// <c>UseHttp1s</c> / <c>UseHttp2s</c> completes a real TLS handshake with a self-signed certificate,
/// reports transport security, and serves a request that carries the <c>https</c> scheme. One test
/// drives the full builder path (<c>WebApplicationBuilder.Server.UseServer(options =&gt;
/// options.UseHttp1s(...))</c>) to prove the composition root wires the secured listener into the
/// running server.
/// </summary>
public class WebTlsHostingIntegrationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should serve an HTTP/1.1 request over TLS with the https scheme")]
    public async Task UseHttp1s_OverTls_ShouldServeRequestWithHttpsScheme()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int port = GetAvailableLoopbackPort();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        HttpConnectionListenerOptions options = new();
        options.UseHttp1s(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            new TlsServerOptions
            {
                AuthenticationOptions = { ServerCertificate = certificate }
            });

        await using HttpConnectionListener listener = new(options);

        TaskCompletionSource<HttpScheme> observedScheme = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = RunSingleRequestServerAsync(listener, observedScheme, cancellationToken);

        try
        {
            // Act — a real HTTP/1.1-over-TLS request from the .NET client.
            using HttpResponseMessage response = await SendWithRetryAsync(
                new Uri($"https://127.0.0.1:{port}/secure?probe=h1"),
                NetHttpVersion.Version11,
                cancellationToken);

            // Assert — the request completed over TLS and the transport-derived scheme is https.
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            HttpScheme scheme = await observedScheme.Task.WaitAsync(cancellationToken);
            scheme.ShouldBe(HttpScheme.Https);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveAsync(serverTask);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should serve an HTTP/2 request over TLS negotiated via the default h2 ALPN")]
    public async Task UseHttp2s_OverTls_ShouldServeRequestWithHttpsSchemeAndNegotiateH2()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int port = GetAvailableLoopbackPort();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        // No ApplicationProtocols supplied — UseHttp2s defaults ALPN to h2, which is what makes the
        // client negotiate HTTP/2 over the secured listener.
        HttpConnectionListenerOptions options = new();
        options.UseHttp2s(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            new TlsServerOptions
            {
                AuthenticationOptions = { ServerCertificate = certificate }
            });

        await using HttpConnectionListener listener = new(options);

        TaskCompletionSource<HttpScheme> observedScheme = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serverTask = RunSingleRequestServerAsync(listener, observedScheme, cancellationToken);

        try
        {
            // Act — request HTTP/2 exactly; success proves the defaulted h2 ALPN id negotiated.
            using HttpResponseMessage response = await SendWithRetryAsync(
                new Uri($"https://127.0.0.1:{port}/secure?probe=h2"),
                NetHttpVersion.Version20,
                cancellationToken);

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            response.Version.ShouldBe(NetHttpVersion.Version20);
            HttpScheme scheme = await observedScheme.Task.WaitAsync(cancellationToken);
            scheme.ShouldBe(HttpScheme.Https);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveAsync(serverTask);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Builder path: UseServer(options => options.UseHttp1s(...)) should serve a request over TLS with the https scheme")]
    public async Task WebApplicationBuilderServerUseHttp1s_OverTls_ShouldServeRequestWithHttpsScheme()
    {
        // Arrange — the full composition-root path: the secured listener is registered inside
        // builder.Server.UseServer(...), then the built server serves it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int port = GetAvailableLoopbackPort();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options =>
        {
            options.UseHttp1s(
                tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port),
                new TlsServerOptions
                {
                    AuthenticationOptions = { ServerCertificate = certificate }
                });
        });

        WebApplication app = builder.Build();

        // A terminal middleware observes the transport-derived scheme and answers an empty 200. It
        // does not chain to next: a handler that produces the final (bodyless) response is terminal,
        // and the pipeline's #881 terminal now reads a bodyless-200 fall-through as unhandled (404).
        TaskCompletionSource<HttpScheme> observedScheme = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            observedScheme.TrySetResult(context.Request.Scheme);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        // Resolve and drive the default server directly. StartAsync awaits the TCP bind before it
        // launches the accept loop, so returning here means the endpoint is ready for the client.
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellationToken);

        try
        {
            // Act
            using HttpResponseMessage response = await SendWithRetryAsync(
                new Uri($"https://127.0.0.1:{port}/secure"),
                NetHttpVersion.Version11,
                cancellationToken);

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            HttpScheme scheme = await observedScheme.Task.WaitAsync(cancellationToken);
            scheme.ShouldBe(HttpScheme.Https);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttps: Should serve an HTTP/1.1 client and an HTTP/2 client on the same TLS port")]
    public async Task UseHttps_OverTls_ShouldServeHttp11AndHttp2ClientsOnOnePort()
    {
        // Arrange — one TLS endpoint offering h2 and http/1.1 (the UseHttps default), served through the
        // full composition root.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int port = GetAvailableLoopbackPort();
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options =>
        {
            options.UseHttps(
                tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port),
                new TlsServerOptions
                {
                    AuthenticationOptions = { ServerCertificate = certificate }
                });
        });

        WebApplication app = builder.Build();

        ConcurrentDictionary<string, (Assimalign.Cohesion.Http.HttpVersion Version, HttpScheme Scheme)> observed = new();
        app.Use((context, next) =>
        {
            observed[context.Request.Path.ToString()] = (context.Version, context.Request.Scheme);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellationToken);

        try
        {
            // Act — four clients reach the same port: the .NET client asking for HTTP/1.1 (it sends no
            // ALPN extension), the .NET client asking for HTTP/2 (it offers h2), and two raw TLS clients,
            // one offering only http/1.1 and one sending no ALPN extension at all.
            using HttpResponseMessage http11 = await SendWithRetryAsync(
                new Uri($"https://127.0.0.1:{port}/h1"),
                NetHttpVersion.Version11,
                cancellationToken);
            using HttpResponseMessage http2 = await SendWithRetryAsync(
                new Uri($"https://127.0.0.1:{port}/h2"),
                NetHttpVersion.Version20,
                cancellationToken);
            (SslApplicationProtocol http11Protocol, string http11Response) = await SendRawHttp11Async(
                port, "/alpn-http11", [SslApplicationProtocol.Http11], cancellationToken);
            (SslApplicationProtocol noAlpnProtocol, string noAlpnResponse) = await SendRawHttp11Async(
                port, "/no-alpn", applicationProtocols: null, cancellationToken);

            // Assert — the client and the server agree on the protocol of every connection.
            http11.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            http11.Version.ShouldBe(NetHttpVersion.Version11);
            http2.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            http2.Version.ShouldBe(NetHttpVersion.Version20);
            http11Protocol.ShouldBe(SslApplicationProtocol.Http11);
            http11Response.ShouldStartWith("HTTP/1.1 200", Case.Sensitive);
            noAlpnProtocol.Protocol.IsEmpty.ShouldBeTrue();
            noAlpnResponse.ShouldStartWith("HTTP/1.1 200", Case.Sensitive);

            observed["/h1"].ShouldBe((Assimalign.Cohesion.Http.HttpVersion.Http11, HttpScheme.Https));
            observed["/h2"].ShouldBe((Assimalign.Cohesion.Http.HttpVersion.Http20, HttpScheme.Https));
            observed["/alpn-http11"].ShouldBe((Assimalign.Cohesion.Http.HttpVersion.Http11, HttpScheme.Https));
            observed["/no-alpn"].ShouldBe((Assimalign.Cohesion.Http.HttpVersion.Http11, HttpScheme.Https));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends one HTTP/1.1 request over a TLS connection whose handshake offers
    /// <paramref name="applicationProtocols"/> through ALPN (no ALPN extension at all when
    /// <see langword="null"/>), and returns what the handshake negotiated together with the raw response.
    /// </summary>
    private static async Task<(SslApplicationProtocol Protocol, string Response)> SendRawHttp11Async(
        int port,
        string path,
        List<SslApplicationProtocol>? applicationProtocols,
        CancellationToken cancellationToken)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        await using SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = applicationProtocols,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            },
            cancellationToken).ConfigureAwait(false);

        byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await ssl.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);

        using StreamReader reader = new(ssl, Encoding.ASCII);
        string response = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return (ssl.NegotiatedApplicationProtocol, response);
    }

    /// <summary>
    /// Accepts a single connection, drives its first request through the transport, records the
    /// request scheme, and answers 200. Any fault is surfaced through <paramref name="observedScheme"/>
    /// so the awaiting test observes it rather than losing it on a background task.
    /// </summary>
    private static async Task RunSingleRequestServerAsync(
        HttpConnectionListener listener,
        TaskCompletionSource<HttpScheme> observedScheme,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
            IHttpConnectionContext context = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await foreach (IHttpContext exchange in context.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                observedScheme.TrySetResult(exchange.Request.Scheme);

                exchange.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.SendAsync(exchange, cancellationToken).ConfigureAwait(false);
                await exchange.DisposeAsync().ConfigureAwait(false);
                break;
            }
        }
        catch (Exception exception)
        {
            observedScheme.TrySetException(exception);
        }
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, Version httpVersion, CancellationToken cancellationToken)
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

            using HttpRequestMessage request = new(ClientHttpMethod.Get, uri)
            {
                Version = httpVersion,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            try
            {
                return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                exception.InnerException is SocketException && !cancellationToken.IsCancellationRequested)
            {
                // The listener has not bound yet (connection refused); retry until the shared timeout
                // cancels. A TLS/protocol failure carries a different inner exception and propagates.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The test's assertions own the verdict; drain the server task quietly on teardown so a
            // cancellation/dispose fault does not surface as an unobserved task exception.
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
