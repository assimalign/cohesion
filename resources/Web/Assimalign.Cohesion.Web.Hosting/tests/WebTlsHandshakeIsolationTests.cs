using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// End-to-end coverage for issue #1304 over real loopback sockets: on one TLS endpoint, a client that sends
/// bytes that are not a TLS handshake, a client that stays silent, and a client the certificate policy
/// refuses each fail alone. Each failed connection is closed by the server, and a well-behaved client is
/// served on the same endpoint, during the silent client's handshake as well as after each failure. The
/// HTTP/3 case covers the QUIC endpoint, whose failed handshakes System.Net.Quic reports from its accept.
/// </summary>
public class WebTlsHandshakeIsolationTests
{
    // A hang guard, never a budget: before #1304 each of these waits hung until it expired.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttps: Bytes that are not a TLS handshake should close only that connection")]
    public async Task UseHttps_AfterClientSendsNonTlsBytes_ShouldCloseThatConnectionAndServeTheNext()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        await using TlsTestServer server = await TlsTestServer.StartAsync(CreateServerOptions(certificate), cancellation.Token);
        using Socket garbage = await ConnectAsync(server.Port, cancellation.Token);

        // Act — ten bytes that are not a TLS ClientHello; the server must close that connection itself.
        await garbage.SendAsync("HELLO-1304"u8.ToArray(), SocketFlags.None, cancellation.Token);
        await WaitForServerCloseAsync(garbage, cancellation.Token);
        using HttpResponseMessage response = await SendAsync(server.Port, NetHttpVersion.Version11, clientCertificate: null, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttps: A silent client should not delay another client")]
    public async Task UseHttps_WhileAClientStaysSilent_ShouldServeAnotherClient()
    {
        // Arrange — left alone, the silent client would hold its handshake for minutes.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        TlsServerOptions options = CreateServerOptions(certificate);
        options.HandshakeTimeout = TimeSpan.FromMinutes(5);
        await using TlsTestServer server = await TlsTestServer.StartAsync(options, cancellation.Token);
        using Socket silent = await ConnectAsync(server.Port, cancellation.Token);

        // Act
        using HttpResponseMessage response = await SendAsync(server.Port, NetHttpVersion.Version20, clientCertificate: null, cancellation.Token);

        // Assert — served while the silent client's connection is still open and still unanswered.
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        silent.Poll(0, SelectMode.SelectRead).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttps: A handshake that times out should close its connection and keep the endpoint serving")]
    public async Task UseHttps_WhenASilentClientExceedsTheHandshakeTimeout_ShouldCloseItAndKeepServing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        TlsServerOptions options = CreateServerOptions(certificate);
        options.HandshakeTimeout = TimeSpan.FromSeconds(3);
        await using TlsTestServer server = await TlsTestServer.StartAsync(options, cancellation.Token);
        using Socket silent = await ConnectAsync(server.Port, cancellation.Token);

        // Act — the server gives up on the silent client once its handshake times out; a client that
        // arrives afterwards is served.
        await WaitForServerCloseAsync(silent, cancellation.Token);
        using HttpResponseMessage response = await SendAsync(server.Port, NetHttpVersion.Version11, clientCertificate: null, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - RequireClientCertificate: A client refused for want of a certificate should close only its connection")]
    public async Task RequireClientCertificate_AfterRefusingAClientWithoutACertificate_ShouldCloseItAndServeTheNext()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        string expected = clientCertificate.Thumbprint;
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == expected),
            cancellation.Token);
        using Socket refused = await ConnectAsync(server.Port, cancellation.Token);

        // Act — a client without a certificate attempts its handshake and is refused.
        await AttemptHandshakeWithoutCertificateAsync(refused, cancellation.Token);
        await WaitForServerCloseAsync(refused, cancellation.Token);
        using HttpResponseMessage response = await SendAsync(server.Port, NetHttpVersion.Version20, clientCertificate, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        ObservedSession session = await server.Observed.Task.WaitAsync(cancellation.Token);
        session.ClientCertificateThumbprint.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: A client refused for want of a certificate should not stop the endpoint")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task UseHttp3_AfterRefusingAClientWithoutACertificate_ShouldServeTheNext()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        string expected = clientCertificate.Thumbprint;
        int port = GetAvailableUdpPort();
        TlsServerOptions tls = CreateServerOptions(certificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == expected);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp3(quic => quic.EndPoint = new IPEndPoint(IPAddress.Loopback, port), tls));
        WebApplication app = builder.Build();
        app.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellation.Token);

        try
        {
            // Act
            await Should.ThrowAsync<HttpRequestException>(
                () => SendAsync(port, NetHttpVersion.Version30, clientCertificate: null, cancellation.Token));
            using HttpResponseMessage response = await SendAsync(port, NetHttpVersion.Version30, clientCertificate, cancellation.Token);

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            response.Version.ShouldBe(NetHttpVersion.Version30);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static TlsServerOptions CreateServerOptions(X509Certificate2 certificate)
    {
        return new TlsServerOptions
        {
            AuthenticationOptions = { ServerCertificate = certificate }
        };
    }

    private static async Task<Socket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads until the server ends the connection: a zero-byte read (its FIN) or a reset both mean the
    /// server closed its socket. Bytes it sent first, such as a TLS alert, are drained.
    /// </summary>
    private static async Task WaitForServerCloseAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];

        try
        {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken) > 0)
            {
            }
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
        }
    }

    /// <summary>
    /// Runs a TLS client handshake that presents no certificate over <paramref name="socket"/>, leaving the
    /// socket open. Under TLS 1.3 the client can finish its side before the server checks for a
    /// certificate, so the refusal may arrive after this returns.
    /// </summary>
    private static async Task AttemptHandshakeWithoutCertificateAsync(Socket socket, CancellationToken cancellationToken)
    {
        await using SslStream ssl = new(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: true);

        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            // Refused during the handshake.
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(int port, Version version, X509Certificate2? clientCertificate, CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = new()
        {
            SslOptions =
            {
                // The server certificate is a throwaway self-signed test certificate.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };

        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
        }

        using HttpClient client = new(handler, disposeHandler: true);
        using HttpRequestMessage request = new(ClientHttpMethod.Get, new Uri($"https://127.0.0.1:{port}/"))
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static int GetAvailableUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
