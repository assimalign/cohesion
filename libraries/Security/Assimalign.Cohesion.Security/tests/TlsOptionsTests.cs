using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Security.Tests;

public class TlsOptionsTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly TestCertificateFixture _fixture;

    public TlsOptionsTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Ctor_OnTlsServerOptions_ShouldDefaultToTenSecondTimeoutAndAuthenticationOptions()
    {
        // Arrange & Act
        TlsServerOptions options = new();

        // Assert
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.AuthenticationOptions.ShouldNotBeNull();
    }

    [Fact]
    public void Ctor_OnTlsClientOptions_ShouldDefaultToTenSecondTimeoutAndAuthenticationOptions()
    {
        // Arrange & Act
        TlsClientOptions options = new();

        // Assert
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.AuthenticationOptions.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpgradeToTlsAsync_AsClientAgainstSilentPeer_ShouldCancelAfterHandshakeTimeout()
    {
        // Arrange: the server end never participates, so the client handshake can only time out.
        (TestPipeConnection client, TestPipeConnection _) = InMemoryConnectionPair.Create();
        TlsClientOptions options = new()
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(100),
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        // Act
        Exception? exception = await RecordWithinTestTimeoutAsync(() => client.UpgradeToTlsAsync(options).AsTask());

        // Assert
        exception.ShouldNotBeNull();
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task UpgradeToTlsAsync_AsServerAgainstSilentPeer_ShouldCancelAfterHandshakeTimeout()
    {
        // Arrange: no client hello ever arrives, so the server handshake can only time out.
        (TestPipeConnection _, TestPipeConnection server) = InMemoryConnectionPair.Create();
        TlsServerOptions options = new()
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(100),
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate
            }
        };

        // Act
        Exception? exception = await RecordWithinTestTimeoutAsync(() => server.UpgradeToTlsAsync(options).AsTask());

        // Assert
        exception.ShouldNotBeNull();
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task UpgradeToTlsAsync_WithAlreadyCanceledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        (TestPipeConnection client, TestPipeConnection _) = InMemoryConnectionPair.Create();
        TlsClientOptions options = new()
        {
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };
        using CancellationTokenSource canceled = new();
        canceled.Cancel();

        // Act
        Exception? exception = await RecordWithinTestTimeoutAsync(() => client.UpgradeToTlsAsync(options, canceled.Token).AsTask());

        // Assert
        exception.ShouldNotBeNull();
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    /// Records the exception thrown by <paramref name="action"/>, failing fast if it neither
    /// completes nor faults within the test timeout (so a broken timeout cannot hang the run).
    /// </summary>
    private static async Task<Exception?> RecordWithinTestTimeoutAsync(Func<Task> action)
    {
        Task<Exception?> record = Record.ExceptionAsync(action);
        Task completed = await Task.WhenAny(record, Task.Delay(TestTimeout));

        completed.ShouldBeSameAs(record, "The TLS handshake neither completed nor faulted within the test timeout.");

        return await record;
    }
}
