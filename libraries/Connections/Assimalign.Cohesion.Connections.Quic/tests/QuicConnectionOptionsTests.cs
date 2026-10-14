using System;
using System.Net.Quic;
using System.Runtime.Versioning;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicConnectionOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - Handshake timeout: Listener options should preserve the System.Net.Quic ten-second default")]
    public void HandshakeTimeout_OnDefaultListenerOptions_ShouldMatchQuicDefault()
    {
        // Arrange
        QuicConnectionListenerOptions options = new();

        // Act — this is the same options factory used by the listener's handshake callback.
        QuicServerConnectionOptions connectionOptions = options.CreateConnectionOptions(options.MaxBidirectionalStreamCount);

        // Assert — no native provider is needed to construct or inspect these options.
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        connectionOptions.HandshakeTimeout.ShouldBe(new QuicServerConnectionOptions().HandshakeTimeout);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Handshake timeout: The listener callback options should carry finite, zero, and infinite timeouts")]
    [InlineData(2750)]
    [InlineData(0)]
    [InlineData(-1)]
    public void HandshakeTimeout_OnConnectionOptions_ShouldFlowConfiguredTimeout(int milliseconds)
    {
        // Arrange — the argument is the bind-time stream limit, even if the original option changes.
        TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);
        QuicConnectionListenerOptions options = new()
        {
            HandshakeTimeout = timeout,
            MaxBidirectionalStreamCount = 99
        };

        // Act
        QuicServerConnectionOptions connectionOptions = options.CreateConnectionOptions(maxInboundBidirectionalStreams: 7);

        // Assert
        connectionOptions.HandshakeTimeout.ShouldBe(timeout);
        connectionOptions.MaxInboundBidirectionalStreams.ShouldBe(7);
        connectionOptions.ServerAuthenticationOptions.ShouldBeSameAs(options.ServerAuthenticationOptions);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Handshake timeout: Negative values other than infinite should be rejected")]
    [InlineData(-1L)]
    [InlineData(-9999L)]
    [InlineData(-10001L)]
    public void HandshakeTimeout_WhenNegativeAndNotInfinite_ShouldThrow(long ticks)
    {
        // Arrange
        QuicConnectionListenerOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = TimeSpan.FromTicks(ticks));

        // Assert
        exception.ParamName.ShouldBe("value");
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void DefaultErrorCodes_OnListenerOptions_ShouldMatchHttp3Codes()
    {
        // Arrange / Act
        // The options default ApplicationProtocols to HTTP/3, so the error codes default to
        // the matching RFC 9114 §8.1 values.
        QuicConnectionListenerOptions options = new();

        // Assert
        options.DefaultCloseErrorCode.ShouldBe(0x100);  // H3_NO_ERROR
        options.DefaultStreamErrorCode.ShouldBe(0x10c); // H3_REQUEST_CANCELLED
    }

    [Fact]
    public void DefaultErrorCodes_OnFactoryOptions_ShouldMatchHttp3Codes()
    {
        // Arrange / Act
        // The options default ApplicationProtocols to HTTP/3, so the error codes default to
        // the matching RFC 9114 §8.1 values.
        QuicConnectionFactoryOptions options = new();

        // Assert
        options.DefaultCloseErrorCode.ShouldBe(0x100);  // H3_NO_ERROR
        options.DefaultStreamErrorCode.ShouldBe(0x10c); // H3_REQUEST_CANCELLED
    }
}
