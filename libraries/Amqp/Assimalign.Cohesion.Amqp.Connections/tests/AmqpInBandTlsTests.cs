using System;
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Security;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs in-band TLS (AMQP 1.0 Part 5, section 5.2) end to end: a listener and a factory, each with a
/// <see cref="TlsConnectionLayer"/>, establish over an in-memory carrier with a real TLS handshake, and the stand-in
/// <see cref="AmqpTestTlsLayer"/> pins what becomes of the secured connection when establishment fails over it. The
/// header-phase corpus (rule H21) pins the header exchange around the handshake byte by byte.
/// </summary>
/// <remarks>
/// A listener reports a failed establishment only through the <c>Assimalign.Cohesion.Connections</c> event source,
/// so these tests share the header-phase collection, which runs alone.
/// </remarks>
[Collection(AmqpHeaderPhaseCollection.Name)]
public class AmqpInBandTlsTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly TestCertificateFixture _fixture;

    public AmqpInBandTlsTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should run SASL and the connection over TLS when both sides negotiate it in band")]
    public async Task ConnectAsync_WithInBandTlsOnBothSides_ShouldRunSaslAndConnectionOverTls()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new() { TlsLayer = CreateServerLayer(), RequireTls = true };
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        AmqpConnectionFactoryOptions factoryOptions = new()
        {
            TlsLayer = CreateClientLayer(),
            SaslMechanism = new AmqpSaslAnonymousClientMechanism()
        };

        // Act
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, factoryOptions, timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody("over TLS") }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        ITlsConnectionInfo clientTls = host.Client.Tls.ShouldNotBeNull();
        ITlsConnectionInfo serverTls = host.Server.Tls.ShouldNotBeNull();
        clientTls.TlsProtocol.ShouldBeOneOf(SslProtocols.Tls12, SslProtocols.Tls13);
        serverTls.TlsProtocol.ShouldBe(clientTls.TlsProtocol);
        clientTls.RemoteCertificate.ShouldNotBeNull().GetCertHashString().ShouldBe(_fixture.Certificate.GetCertHashString());
        host.Client.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        host.Server.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        host.Client.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
        host.Server.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
        received.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("over TLS");
        outcome.ShouldBeOfType<AmqpAccepted>();
        host.Listener.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        host.Factory.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail on both sides when the listener requires TLS and the factory does not ask for it")]
    public async Task ConnectAsync_WhenListenerRequiresTlsAndFactoryHasNoLayer_ShouldFailOnBothSides()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpUpgradeFailureRecorder failures = new();
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        AmqpConnectionListener listener = CreateListener(serverCarrier, new AmqpConnectionListenerOptions { TlsLayer = CreateServerLayer(), RequireTls = true });
        AmqpConnectionFactory factory = CreateFactory(clientCarrier, new AmqpConnectionFactoryOptions());
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();

        try
        {
            // Act
            AmqpException exception = await Should.ThrowAsync<AmqpException>(factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());
            (string exceptionType, string exceptionMessage) = await failures.WaitForFailureAsync(serverCarrier.Id, timeout.Token);

            // Assert
            exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
            exception.Message.ShouldBe(ErrorMessages.TlsRequiredByPeer);
            exceptionType.ShouldBe(typeof(AmqpException).FullName);
            exceptionMessage.ShouldBe(ErrorMessages.TlsRequired);
            clientCarrier.IsDisposed.ShouldBeTrue();
            accept.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
            await Record.ExceptionAsync(() => accept);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail rather than continue without TLS when the listener declines it")]
    public async Task ConnectAsync_WhenListenerDeclinesTls_ShouldFailWithoutFallingBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpUpgradeFailureRecorder failures = new();
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        AmqpConnectionListener listener = CreateListener(serverCarrier, new AmqpConnectionListenerOptions());
        AmqpConnectionFactory factory = CreateFactory(clientCarrier, new AmqpConnectionFactoryOptions { TlsLayer = CreateClientLayer() });
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();

        try
        {
            // Act
            AmqpException exception = await Should.ThrowAsync<AmqpException>(factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());
            (string exceptionType, string exceptionMessage) = await failures.WaitForFailureAsync(serverCarrier.Id, timeout.Token);

            // Assert
            exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
            exception.Message.ShouldBe(ErrorMessages.TlsNotOffered);
            exceptionType.ShouldBe(typeof(AmqpException).FullName);
            exceptionMessage.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProtocolHeaderUnsupported, 2, 1, 0, 0));
            clientCarrier.IsDisposed.ShouldBeTrue();
            accept.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
            await Record.ExceptionAsync(() => accept);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail with the TLS layer's exception when the client does not trust the server's certificate")]
    public async Task ConnectAsync_OnUntrustedServerCertificate_ShouldFailWithTheLayersException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpUpgradeFailureRecorder failures = new();
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        AmqpConnectionListener listener = CreateListener(serverCarrier, new AmqpConnectionListenerOptions { TlsLayer = CreateServerLayer() });
        TlsConnectionLayer distrusting = new(new TlsClientOptions
        {
            AuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = TestCertificateFixture.HostName,
                RemoteCertificateValidationCallback = static (_, _, _, _) => false
            }
        });
        AmqpConnectionFactory factory = CreateFactory(clientCarrier, new AmqpConnectionFactoryOptions { TlsLayer = distrusting });
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();

        try
        {
            // Act
            await Should.ThrowAsync<AuthenticationException>(factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());
            (string exceptionType, string _) = await failures.WaitForFailureAsync(serverCarrier.Id, timeout.Token);

            // Assert
            exceptionType.ShouldNotBeNullOrEmpty();
            clientCarrier.IsDisposed.ShouldBeTrue();
            accept.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
            await Record.ExceptionAsync(() => accept);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should dispose the connection the TLS layer returned when establishment fails over it")]
    public async Task AcceptAsync_OnFailureAfterTls_ShouldDisposeSecuredConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpUpgradeFailureRecorder failures = new();
        AmqpTestTlsLayer layer = new();
        AmqpConnectionListenerOptions options = new() { TlsLayer = layer };
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);

        try
        {
            // Act: the peer secures the connection, then skips the SASL the listener requires.
            await peer.WriteAsync(AmqpHeaderPhaseCorpus.TlsHeader, timeout.Token);
            (await peer.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpHeaderPhaseCorpus.TlsHeader);
            (await peer.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpTestTlsLayer.Hello);
            await peer.WriteAsync(AmqpTestTlsLayer.Hello, timeout.Token);
            await peer.WriteAsync(AmqpHeaderPhaseCorpus.AmqpHeader, timeout.Token);
            (await peer.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpHeaderPhaseCorpus.SaslHeader);
            (string _, string exceptionMessage) = await failures.WaitForFailureAsync(peer.Carrier.Id, timeout.Token);

            // Assert
            exceptionMessage.ShouldBe(ErrorMessages.SaslRequired);
            layer.Secured.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
            accept.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await peer.DisposeAsync();
            await listener.DisposeAsync();
            await Record.ExceptionAsync(() => accept);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should dispose the connection the TLS layer returned when establishment fails over it")]
    public async Task ConnectAsync_OnFailureAfterTls_ShouldDisposeSecuredConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpTestTlsLayer layer = new();
        (AmqpTestPeer server, AmqpConnectionFactory _, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(new AmqpConnectionFactoryOptions { TlsLayer = layer }, timeout.Token);

        try
        {
            // Act: the server secures the connection, then answers the AMQP header with the TLS header again.
            (await server.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpHeaderPhaseCorpus.TlsHeader);
            await server.WriteAsync(AmqpHeaderPhaseCorpus.TlsHeader, timeout.Token);
            (await server.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpTestTlsLayer.Hello);
            await server.WriteAsync(AmqpTestTlsLayer.Hello, timeout.Token);
            (await server.ReadProtocolHeaderAsync(timeout.Token)).ShouldBe(AmqpHeaderPhaseCorpus.AmqpHeader);
            await server.WriteAsync(AmqpHeaderPhaseCorpus.TlsHeader, timeout.Token);
            AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

            // Assert
            exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProtocolHeaderUnsupported, 2, 1, 0, 0));
            layer.Secured.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
            server.Carrier.IsDisposed.ShouldBeTrue();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should refuse to require TLS without a layer over a carrier that does not secure its connections")]
    public async Task Create_OnRequiredTlsWithoutLayer_ShouldThrow()
    {
        // Arrange
        await using TestConnectionListener carrier = new();
        AmqpConnectionListenerOptions options = new() { RequireTls = true };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(carrier, options));

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith(ErrorMessages.TlsLayerMissing);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should take a connection its carrier secured as meeting required TLS, without a TLS header")]
    public async Task AcceptAsync_OnRequiredTlsOverSecuredCarrier_ShouldOpenWithoutTlsHeader()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        ConnectionCapabilities secured = TestConnection.DefaultCapabilities with { Security = ConnectionSecurity.Tls };
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair(secured);
        TestConnectionListener carrierListener = new(secured);
        carrierListener.Enqueue(serverCarrier);
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener, new AmqpConnectionListenerOptions { RequireTls = true });
        AmqpConnectionFactory factory = CreateFactory(clientCarrier, new AmqpConnectionFactoryOptions());

        // Act
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        await using IAmqpConnection client = await factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token);
        await using IAmqpConnection server = await accept;

        // Assert
        server.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        listener.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Capabilities: Should report what the TLS layer describes only where every connection goes through it")]
    public async Task Capabilities_WithTlsLayer_ShouldReportTlsOnlyWhereEveryConnectionIsSecured()
    {
        // Arrange
        TestConnectionListener requiring = new();
        TestConnectionListener offering = new();
        TestConnectionFactory asking = new();
        TestConnectionFactory plain = new();

        // Act
        await using AmqpConnectionListener required = AmqpConnectionListener.Create(requiring, new AmqpConnectionListenerOptions { TlsLayer = new AmqpTestTlsLayer(), RequireTls = true });
        await using AmqpConnectionListener offered = AmqpConnectionListener.Create(offering, new AmqpConnectionListenerOptions { TlsLayer = new AmqpTestTlsLayer() });
        AmqpConnectionFactory securing = AmqpConnectionFactory.Create(asking, new AmqpConnectionFactoryOptions { TlsLayer = new AmqpTestTlsLayer() });
        AmqpConnectionFactory unsecured = AmqpConnectionFactory.Create(plain, new AmqpConnectionFactoryOptions());

        // Assert
        required.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        offered.Capabilities.Security.ShouldBe(ConnectionSecurity.None);
        securing.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        unsecured.Capabilities.Security.ShouldBe(ConnectionSecurity.None);
    }

    private static AmqpConnectionListener CreateListener(TestConnection carrier, AmqpConnectionListenerOptions options)
    {
        TestConnectionListener carrierListener = new();

        carrierListener.Enqueue(carrier);
        return AmqpConnectionListener.Create(carrierListener, options);
    }

    private static AmqpConnectionFactory CreateFactory(TestConnection carrier, AmqpConnectionFactoryOptions options)
    {
        TestConnectionFactory carrierFactory = new();

        carrierFactory.Enqueue(carrier);
        return AmqpConnectionFactory.Create(carrierFactory, options);
    }

    private TlsConnectionLayer CreateServerLayer()
    {
        return new TlsConnectionLayer(new TlsServerOptions
        {
            AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = _fixture.Certificate }
        });
    }

    private TlsConnectionLayer CreateClientLayer()
    {
        TestCertificateFixture fixture = _fixture;

        return new TlsConnectionLayer(new TlsClientOptions
        {
            AuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = TestCertificateFixture.HostName,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => fixture.Validate(certificate, errors)
            }
        });
    }
}
