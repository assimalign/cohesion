using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// Covers the client-certificate policy of <see cref="TlsServerOptions"/>
/// (<see cref="TlsServerOptions.RequireClientCertificate"/> and
/// <see cref="TlsServerOptions.AllowClientCertificate"/>): the certificate is requested in the
/// handshake, a missing one is refused or tolerated, and a presented one must pass validation.
/// </summary>
public class TlsClientCertificateTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly TestCertificateFixture _fixture;

    public TlsClientCertificateTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RequireClientCertificate: Should request a certificate in the handshake")]
    public void RequireClientCertificate_OnOptions_ShouldRequestCertificateAndInstallValidator()
    {
        // Arrange
        TlsServerOptions options = new();

        // Act
        TlsServerOptions result = options.RequireClientCertificate();

        // Assert — CertificateRequest is sent during the handshake (RFC 8446 §4.3.2).
        result.ShouldBeSameAs(options);
        options.AuthenticationOptions.ClientCertificateRequired.ShouldBeTrue();
        options.AuthenticationOptions.RemoteCertificateValidationCallback.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RequireClientCertificate: Should complete the handshake for an accepted certificate")]
    public async Task RequireClientCertificate_WithAcceptedCertificate_ShouldCompleteHandshake()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        X509Certificate2? validated = null;
        TlsServerOptions serverOptions = CreateServerOptions().RequireClientCertificate((certificate, _, _) =>
        {
            validated = certificate;
            return true;
        });

        // Act
        (Task<IConnection> server, Task<IConnection> _) = Handshake(serverOptions, _fixture.ClientCertificate, timeout.Token);
        IConnection secured = await server;

        // Assert
        validated.ShouldNotBeNull();
        validated.Thumbprint.ShouldBe(_fixture.ClientCertificate.Thumbprint);
        secured.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate!.Thumbprint.ShouldBe(_fixture.ClientCertificate.Thumbprint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RequireClientCertificate: Should refuse a client that presents no certificate")]
    public async Task RequireClientCertificate_WithoutCertificate_ShouldFailServerHandshake()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions serverOptions = CreateServerOptions().RequireClientCertificate(static (_, _, _) => true);

        // Act
        (Task<IConnection> server, Task<IConnection> client) = Handshake(serverOptions, clientCertificate: null, timeout.Token);

        // Assert
        await Should.ThrowAsync<AuthenticationException>(server);
        await ObserveAsync(client);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RequireClientCertificate: Should refuse a certificate the validator rejects")]
    public async Task RequireClientCertificate_WhenValidatorRejects_ShouldFailServerHandshake()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions serverOptions = CreateServerOptions().RequireClientCertificate(static (_, _, _) => false);

        // Act
        (Task<IConnection> server, Task<IConnection> client) = Handshake(serverOptions, _fixture.ClientCertificate, timeout.Token);

        // Assert
        await Should.ThrowAsync<AuthenticationException>(server);
        await ObserveAsync(client);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - AllowClientCertificate: Should complete the handshake without a certificate")]
    public async Task AllowClientCertificate_WithoutCertificate_ShouldCompleteHandshake()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions serverOptions = CreateServerOptions().AllowClientCertificate(static (_, _, _) => true);

        // Act
        (Task<IConnection> server, Task<IConnection> client) = Handshake(serverOptions, clientCertificate: null, timeout.Token);
        IConnection secured = await server;
        await client;

        // Assert
        secured.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - AllowClientCertificate: Should refuse an untrusted certificate under the platform's default validation")]
    public async Task AllowClientCertificate_WithDefaultValidation_ShouldRefuseUntrustedCertificate()
    {
        // Arrange — the self-signed client certificate does not chain to a root the machine trusts.
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions serverOptions = CreateServerOptions().AllowClientCertificate();

        // Act
        (Task<IConnection> server, Task<IConnection> client) = Handshake(serverOptions, _fixture.ClientCertificate, timeout.Token);

        // Assert
        await Should.ThrowAsync<AuthenticationException>(server);
        await ObserveAsync(client);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - AllowClientCertificate: Should replace an earlier policy these methods installed")]
    public async Task AllowClientCertificate_AfterRequireClientCertificate_ShouldReplacePolicy()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TlsServerOptions serverOptions = CreateServerOptions()
            .RequireClientCertificate(static (_, _, _) => true)
            .AllowClientCertificate(static (_, _, _) => true);

        // Act — the later "allow" policy admits a client without a certificate.
        (Task<IConnection> server, Task<IConnection> client) = Handshake(serverOptions, clientCertificate: null, timeout.Token);
        IConnection secured = await server;
        await client;

        // Assert
        secured.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RequireClientCertificate: Should not overwrite an application's validation callback")]
    public void RequireClientCertificate_WithForeignValidationCallback_ShouldThrow()
    {
        // Arrange
        TlsServerOptions options = new()
        {
            AuthenticationOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => options.RequireClientCertificate());
        Should.Throw<InvalidOperationException>(() => options.AllowClientCertificate());
    }

    private TlsServerOptions CreateServerOptions()
    {
        return new TlsServerOptions
        {
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate
            }
        };
    }

    private static (Task<IConnection> Server, Task<IConnection> Client) Handshake(
        TlsServerOptions serverOptions,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        TlsClientOptions clientOptions = new()
        {
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        if (clientCertificate is not null)
        {
            TlsConnectionInfoTests.UseClientCertificate(clientOptions, clientCertificate);
        }

        Task<IConnection> serverUpgrade = server.UpgradeToTlsAsync(serverOptions, cancellationToken).AsTask();
        Task<IConnection> clientUpgrade = client.UpgradeToTlsAsync(clientOptions, cancellationToken).AsTask();

        return (serverUpgrade, clientUpgrade);
    }

    private static async Task ObserveAsync(Task<IConnection> task)
    {
        try
        {
            IConnection connection = await task;
            await connection.DisposeAsync();
        }
        catch (Exception exception) when (exception is AuthenticationException or System.IO.IOException or OperationCanceledException)
        {
            // Under TLS 1.3 the client may complete its side before the server refuses it, or learn of
            // the refusal from the alert; either way only the server's verdict is under test.
        }
    }
}
