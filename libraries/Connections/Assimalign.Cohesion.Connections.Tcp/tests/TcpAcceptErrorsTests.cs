using System.Net.Sockets;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpAcceptErrorsTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: A reset queued connection should be skipped on every platform")]
    [InlineData(SocketError.ConnectionReset, false)]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, false)]
    [InlineData(SocketError.ConnectionAborted, true)]
    public void IsQueuedConnectionFailure_ResetOrAbort_ShouldBeTrueOnEveryPlatform(SocketError error, bool isLinux)
    {
        // Act
        bool skipped = TcpAcceptErrors.IsQueuedConnectionFailure(error, isLinux, isStreamListener: true);

        // Assert
        skipped.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: A network error Linux reports for a queued connection should be skipped on Linux only")]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.HostDown)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.ProtocolOption)]
    [InlineData(SocketError.OperationNotSupported)]
    public void IsQueuedConnectionFailure_LinuxPendingNetworkError_ShouldBeTrueOnLinuxOnly(SocketError error)
    {
        // Act
        bool skippedOnLinux = TcpAcceptErrors.IsQueuedConnectionFailure(error, isLinux: true, isStreamListener: true);
        bool skippedElsewhere = TcpAcceptErrors.IsQueuedConnectionFailure(error, isLinux: false, isStreamListener: true);

        // Assert
        skippedOnLinux.ShouldBeTrue();
        skippedElsewhere.ShouldBeFalse("on Windows the same value means the listening socket failed");
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: A listening socket that is not a stream socket should not skip network errors")]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.OperationNotSupported)]
    public void IsQueuedConnectionFailure_NonStreamListener_ShouldBeFalse(SocketError error)
    {
        // Act
        bool skipped = TcpAcceptErrors.IsQueuedConnectionFailure(error, isLinux: true, isStreamListener: false);

        // Assert
        skipped.ShouldBeFalse("EOPNOTSUPP means such a socket can never accept, so skipping would spin");
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: Running out of descriptors or buffers should be backed off on every platform")]
    [InlineData(SocketError.TooManyOpenSockets, false)]
    [InlineData(SocketError.TooManyOpenSockets, true)]
    [InlineData(SocketError.NoBufferSpaceAvailable, false)]
    [InlineData(SocketError.NoBufferSpaceAvailable, true)]
    public void IsResourceExhaustion_DescriptorsOrBuffers_ShouldBeTrueOnEveryPlatform(SocketError error, bool isWindows)
    {
        // Act
        bool backedOff = TcpAcceptErrors.IsResourceExhaustion(error, isWindows, isStreamListener: true);

        // Assert
        backedOff.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: An errno .NET does not map should be backed off on Unix only")]
    public void IsResourceExhaustion_UnmappedUnixErrno_ShouldBeTrueOnUnixOnly()
    {
        // Act — ENOMEM, ENOSR, EPROTO and ENONET all arrive as the generic SocketError.SocketError.
        bool backedOffOnUnix = TcpAcceptErrors.IsResourceExhaustion(SocketError.SocketError, isWindows: false, isStreamListener: true);
        bool backedOffOnWindows = TcpAcceptErrors.IsResourceExhaustion(SocketError.SocketError, isWindows: true, isStreamListener: true);
        bool backedOffForNonStream = TcpAcceptErrors.IsResourceExhaustion(SocketError.SocketError, isWindows: false, isStreamListener: false);

        // Assert
        backedOffOnUnix.ShouldBeTrue();
        backedOffOnWindows.ShouldBeFalse();
        backedOffForNonStream.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptErrors: An error of the listening socket itself should be neither skipped nor backed off")]
    [InlineData(SocketError.InvalidArgument)]
    [InlineData(SocketError.NotSocket)]
    [InlineData(SocketError.Fault)]
    public void Classify_ListenerError_ShouldBeNeitherSkippedNorBackedOff(SocketError error)
    {
        // Act
        bool skipped = TcpAcceptErrors.IsQueuedConnectionFailure(error, isLinux: true, isStreamListener: true);
        bool backedOff = TcpAcceptErrors.IsResourceExhaustion(error, isWindows: false, isStreamListener: true);

        // Assert
        skipped.ShouldBeFalse();
        backedOff.ShouldBeFalse();
    }
}
