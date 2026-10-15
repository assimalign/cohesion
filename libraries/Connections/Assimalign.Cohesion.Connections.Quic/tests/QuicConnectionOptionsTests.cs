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
    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Backlog: Negative assignments should be rejected before binding")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Backlog_WhenNegative_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        QuicConnectionListenerOptions options = new() { Backlog = 32 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.Backlog = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.Backlog.ShouldBe(32);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Backlog: Nonnegative assignments should preserve zero and the full integer range")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Backlog_WhenNonnegative_ShouldPreserveValue(int value)
    {
        // Arrange
        QuicConnectionListenerOptions options = new();

        // Act
        options.Backlog = value;

        // Assert
        options.Backlog.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - MaxBidirectionalStreamCount: Out-of-range assignments should be rejected before a handshake")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void MaxBidirectionalStreamCount_WhenOutsideRuntimeRange_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        QuicConnectionListenerOptions options = new() { MaxBidirectionalStreamCount = 7 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.MaxBidirectionalStreamCount = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.MaxBidirectionalStreamCount.ShouldBe(7);
        options.CreateConnectionOptions(options.MaxBidirectionalStreamCount).MaxInboundBidirectionalStreams.ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - MaxUnidirectionalStreamCount: Out-of-range assignments should be rejected before a handshake")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void MaxUnidirectionalStreamCount_WhenOutsideRuntimeRange_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        QuicConnectionListenerOptions options = new() { MaxUnidirectionalStreamCount = 7 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.MaxUnidirectionalStreamCount = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.MaxUnidirectionalStreamCount.ShouldBe(7);
        options.CreateConnectionOptions(options.MaxBidirectionalStreamCount).MaxInboundUnidirectionalStreams.ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Quic] - Inbound stream counts: Runtime boundary values should flow to handshake options")]
    [InlineData(0, 0, 1)]
    [InlineData(0, 65535, 65535)]
    [InlineData(1, 0, 0)]
    [InlineData(65535, 0, 0)]
    [InlineData(65535, 65535, 65535)]
    public void CreateConnectionOptions_WithValidStreamCounts_ShouldPreserveAllowancesAndIdleCloseFallback(
        int bidirectional, int unidirectional, int expectedUnidirectional)
    {
        // Arrange
        QuicConnectionListenerOptions options = new()
        {
            MaxBidirectionalStreamCount = bidirectional,
            MaxUnidirectionalStreamCount = unidirectional
        };

        // Act
        QuicServerConnectionOptions connectionOptions = options.CreateConnectionOptions(options.MaxBidirectionalStreamCount);

        // Assert
        options.MaxBidirectionalStreamCount.ShouldBe(bidirectional);
        options.MaxUnidirectionalStreamCount.ShouldBe(unidirectional);
        connectionOptions.MaxInboundBidirectionalStreams.ShouldBe(bidirectional);
        connectionOptions.MaxInboundUnidirectionalStreams.ShouldBe(expectedUnidirectional);
    }

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
