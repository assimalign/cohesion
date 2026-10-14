using System;
using System.Collections.Generic;
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

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// End-to-end coverage for mutual TLS (issue #1065): an endpoint requests client certificates in the
/// handshake through <see cref="TlsServerOptions.RequireClientCertificate"/> or
/// <see cref="TlsServerOptions.AllowClientCertificate"/>, and the handler reads the connection's TLS
/// session — client certificate, TLS protocol, cipher suite, negotiated application protocol — from
/// <c>context.TlsConnection</c>. Real clients over loopback TLS (and QUIC where the platform supports it),
/// with and without a client certificate.
/// </summary>
public class WebMutualTlsHostingIntegrationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // The .NET client asking for HTTP/1.1 sends no ALPN extension, so its connection negotiates no
    // application protocol (and is served HTTP/1.1); asking for HTTP/2, it offers h2.
    public static IEnumerable<object[]> HttpVersions =>
    [
        [NetHttpVersion.Version11, default(SslApplicationProtocol)],
        [NetHttpVersion.Version20, SslApplicationProtocol.Http2],
    ];

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - AllowClientCertificate: Should serve a client without a certificate and report its TLS session")]
    [MemberData(nameof(HttpVersions))]
    public async Task AllowClientCertificate_WithoutCertificate_ShouldServeAndReportSession(Version version, SslApplicationProtocol expectedProtocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).AllowClientCertificate(static (_, _, _) => true),
            cancellation.Token);

        // Act
        using HttpResponseMessage response = await SendAsync(server.Port, version, clientCertificate: null, cancellation.Token);

        // Assert — served on the requested protocol; the session is reported without a certificate.
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Version.ShouldBe(version);
        ObservedSession session = await server.Observed.Task.WaitAsync(cancellation.Token);
        session.Present.ShouldBeTrue();
        session.ClientCertificateThumbprint.ShouldBeNull();
        session.Protocol.ShouldBeOneOf(SslProtocols.Tls12, SslProtocols.Tls13);
        session.CipherSuite.ShouldNotBe(default);
        session.ApplicationProtocol.ShouldBe(expectedProtocol);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - AllowClientCertificate: Should expose the certificate a client presents")]
    [MemberData(nameof(HttpVersions))]
    public async Task AllowClientCertificate_WithCertificate_ShouldExposeClientCertificate(Version version, SslApplicationProtocol expectedProtocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        string expected = clientCertificate.Thumbprint;
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).AllowClientCertificate((presented, _, _) => presented.Thumbprint == expected),
            cancellation.Token);

        // Act
        using HttpResponseMessage response = await SendAsync(server.Port, version, clientCertificate, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        ObservedSession session = await server.Observed.Task.WaitAsync(cancellation.Token);
        session.ClientCertificateThumbprint.ShouldBe(expected);
        session.ApplicationProtocol.ShouldBe(expectedProtocol);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - RequireClientCertificate: Should serve a client whose certificate passes validation")]
    public async Task RequireClientCertificate_WithAcceptedCertificate_ShouldServe()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        string expected = clientCertificate.Thumbprint;
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == expected),
            cancellation.Token);

        // Act
        using HttpResponseMessage response = await SendAsync(server.Port, NetHttpVersion.Version20, clientCertificate, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        ObservedSession session = await server.Observed.Task.WaitAsync(cancellation.Token);
        session.ClientCertificateThumbprint.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - RequireClientCertificate: Should refuse a client without a certificate")]
    public async Task RequireClientCertificate_WithoutCertificate_ShouldRefuseClient()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).RequireClientCertificate(static (_, _, _) => true),
            cancellation.Token);

        // Act / Assert — the handshake fails, so no request reaches the application.
        await Should.ThrowAsync<HttpRequestException>(
            () => SendAsync(server.Port, NetHttpVersion.Version11, clientCertificate: null, cancellation.Token));
        server.Observed.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - RequireClientCertificate: Should refuse a certificate the validation callback rejects")]
    public async Task RequireClientCertificate_WhenValidatorRejects_ShouldRefuseClient()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            CreateServerOptions(certificate).RequireClientCertificate(static (_, _, _) => false),
            cancellation.Token);

        // Act / Assert
        await Should.ThrowAsync<HttpRequestException>(
            () => SendAsync(server.Port, NetHttpVersion.Version20, clientCertificate, cancellation.Token));
        server.Observed.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - TlsConnection: Should be absent on a cleartext endpoint")]
    public async Task TlsConnection_OnCleartextEndpoint_ShouldBeNull()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int port = GetAvailableTcpPort();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port)));
        WebApplication app = builder.Build();
        TaskCompletionSource<bool> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            observed.TrySetResult(context.TlsConnection is not null);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellation.Token);

        try
        {
            // Act
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/", cancellation.Token);

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await observed.Task.WaitAsync(cancellation.Token)).ShouldBeFalse();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseConfiguration: ClientCertificateMode should request client certificates and validate them against the machine's trust")]
    public async Task UseConfiguration_WithAllowCertificateMode_ShouldRequestAndValidateClientCertificates()
    {
        // Arrange — an Https endpoint from configuration, certificate from a PEM file.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using CertificateFiles files = CertificateFiles.Create();
        using X509Certificate2 untrusted = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        int port = GetAvailableTcpPort();
        IConfiguration configuration = SeededConfiguration.Build(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = "Https",
            ["Http:Endpoints:Secure:Host"] = "127.0.0.1",
            ["Http:Endpoints:Secure:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Http:Endpoints:Secure:Certificate:Path"] = files.PemCertificatePath,
            ["Http:Endpoints:Secure:Certificate:KeyPath"] = files.PemKeyPath,
            ["Http:Endpoints:Secure:ClientCertificateMode"] = "AllowCertificate",
        });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseConfiguration(configuration);
        WebApplication app = builder.Build();
        TaskCompletionSource<bool> sawClientCertificate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            sawClientCertificate.TrySetResult(context.TlsConnection?.ClientCertificate is not null);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellation.Token);

        try
        {
            // Act — a client without a certificate is allowed; one with an untrusted certificate is not.
            using HttpResponseMessage response = await SendAsync(port, NetHttpVersion.Version20, clientCertificate: null, cancellation.Token);
            (await sawClientCertificate.Task.WaitAsync(cancellation.Token)).ShouldBeFalse();

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            await Should.ThrowAsync<HttpRequestException>(
                () => SendAsync(port, NetHttpVersion.Version20, untrusted, cancellation.Token));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should expose the QUIC connection's TLS session and client certificate")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task UseHttp3_WithClientCertificate_ShouldExposeTheQuicSession()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — the same TlsServerOptions policy, given to the QUIC listener.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        using X509Certificate2 clientCertificate = SelfSignedCertificateFactory.CreateClient("cohesion-client");
        string expected = clientCertificate.Thumbprint;
        int port = GetAvailableUdpPort();
        TlsServerOptions tls = CreateServerOptions(certificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == expected);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp3(quic => quic.EndPoint = new IPEndPoint(IPAddress.Loopback, port), tls));
        WebApplication app = builder.Build();
        TaskCompletionSource<ObservedSession> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            observed.TrySetResult(ObservedSession.From(context.TlsConnection));
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellation.Token);

        try
        {
            // Act
            using HttpResponseMessage response = await SendAsync(port, NetHttpVersion.Version30, clientCertificate, cancellation.Token);

            // Assert
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            response.Version.ShouldBe(NetHttpVersion.Version30);
            ObservedSession session = await observed.Task.WaitAsync(cancellation.Token);
            session.ClientCertificateThumbprint.ShouldBe(expected);
            session.Protocol.ShouldBe(SslProtocols.Tls13);
            session.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http3);
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
        using HttpRequestMessage request = new(ClientHttpMethod.Get, new Uri($"https://127.0.0.1:{port}/mtls"))
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static int GetAvailableTcpPort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static int GetAvailableUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
