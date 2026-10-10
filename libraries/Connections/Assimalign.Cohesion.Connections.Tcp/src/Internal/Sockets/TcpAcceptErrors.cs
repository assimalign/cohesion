using System;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// Classifies the errors an accept on a listening socket can fail with, so the listener keeps accepting
/// through every error that does not leave the listening socket itself unable to accept.
/// </summary>
/// <remarks>
/// <para>
/// An error is one of three kinds. A <em>queued-connection failure</em> belongs to the one connection the
/// accept was taking: the listener skips it and accepts the next (#1308). A <em>resource exhaustion</em>
/// means the process or the system ran out of descriptors or buffers: the listener waits with a bounded
/// back-off and retries, because the condition clears once something is freed and retrying at once would
/// spin (#1312). Every other error is the listening socket's own and escapes.
/// </para>
/// <para>
/// .NET reports an accept error as a <see cref="SocketError"/> only. On Unix it maps the native
/// <c>errno</c> through a fixed table, discards the <c>errno</c>, and reports any value outside the table
/// as <see cref="SocketError.SocketError"/>, so the classification has to work from those values.
/// </para>
/// <para>
/// Only a stream listening socket can fail an accept for a queued connection or for want of resources.
/// An inherited descriptor that is not a stream socket fails every accept for good (Linux reports
/// <c>EOPNOTSUPP</c> for it), so for such a socket every error but a reset escapes, and the listener
/// cannot skip or back off forever.
/// </para>
/// </remarks>
internal static class TcpAcceptErrors
{
    /// <summary>
    /// Returns whether <paramref name="error"/> belongs to the queued connection an accept on
    /// <paramref name="listenerSocket"/> was taking, on the platform this process runs on.
    /// </summary>
    /// <param name="error">The error the accept failed with.</param>
    /// <param name="listenerSocket">The listening socket the accept ran on.</param>
    /// <returns><see langword="true"/> when the listener should skip the failure and accept the next connection.</returns>
    public static bool IsQueuedConnectionFailure(SocketError error, Socket listenerSocket)
        => IsQueuedConnectionFailure(error, OperatingSystem.IsLinux(), IsStream(listenerSocket));

    /// <summary>
    /// Returns whether <paramref name="error"/> belongs to the queued connection the accept was taking.
    /// </summary>
    /// <param name="error">The error the accept failed with.</param>
    /// <param name="isLinux">Whether the accept ran on Linux.</param>
    /// <param name="isStreamListener">Whether the listening socket is a stream socket.</param>
    /// <returns><see langword="true"/> when the listener should skip the failure and accept the next connection.</returns>
    /// <remarks>
    /// <para>
    /// A client that resets (RST) while its connection waits in the accept queue makes Windows fail the
    /// accept with <see cref="SocketError.ConnectionReset"/>, and BSD-derived stacks, and Linux in some
    /// cases, with <see cref="SocketError.ConnectionAborted"/>.
    /// </para>
    /// <para>
    /// Linux <c>accept(2)</c> also passes network errors already pending on the new socket back as the
    /// accept's error, and its manual says to retry them like <c>EAGAIN</c>. <c>ENETDOWN</c>,
    /// <c>ENETUNREACH</c>, <c>EHOSTDOWN</c>, <c>EHOSTUNREACH</c>, <c>ENOPROTOOPT</c> and <c>EOPNOTSUPP</c>
    /// arrive here as <see cref="SocketError.NetworkDown"/>, <see cref="SocketError.NetworkUnreachable"/>,
    /// <see cref="SocketError.HostDown"/>, <see cref="SocketError.HostUnreachable"/>,
    /// <see cref="SocketError.ProtocolOption"/> and <see cref="SocketError.OperationNotSupported"/>. Those
    /// values mean the listening socket failed on Windows, so they are skipped on Linux only. The other
    /// two errors the manual lists, <c>EPROTO</c> and <c>ENONET</c>, have no <see cref="SocketError"/> of
    /// their own: they arrive as <see cref="SocketError.SocketError"/> and are backed off instead (see
    /// <see cref="IsResourceExhaustion(SocketError, bool, bool)"/>).
    /// </para>
    /// <para>
    /// Each of these errors consumes the queued connection it reports, so none can recur without a new
    /// connection, and skipping them cannot spin.
    /// </para>
    /// </remarks>
    internal static bool IsQueuedConnectionFailure(SocketError error, bool isLinux, bool isStreamListener) => error switch
    {
        SocketError.ConnectionReset or SocketError.ConnectionAborted => true,
        SocketError.NetworkDown or
        SocketError.NetworkUnreachable or
        SocketError.HostDown or
        SocketError.HostUnreachable or
        SocketError.ProtocolOption or
        SocketError.OperationNotSupported => isLinux && isStreamListener,
        _ => false,
    };

    /// <summary>
    /// Returns whether <paramref name="error"/> reports that the process or the system ran out of
    /// descriptors or buffers while an accept ran on <paramref name="listenerSocket"/>, on the platform
    /// this process runs on.
    /// </summary>
    /// <param name="error">The error the accept failed with.</param>
    /// <param name="listenerSocket">The listening socket the accept ran on.</param>
    /// <returns><see langword="true"/> when the listener should wait and retry the accept.</returns>
    public static bool IsResourceExhaustion(SocketError error, Socket listenerSocket)
        => IsResourceExhaustion(error, OperatingSystem.IsWindows(), IsStream(listenerSocket));

    /// <summary>
    /// Returns whether <paramref name="error"/> reports that the process or the system ran out of
    /// descriptors or buffers.
    /// </summary>
    /// <param name="error">The error the accept failed with.</param>
    /// <param name="isWindows">Whether the accept ran on Windows.</param>
    /// <param name="isStreamListener">Whether the listening socket is a stream socket.</param>
    /// <returns><see langword="true"/> when the listener should wait and retry the accept.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="SocketError.TooManyOpenSockets"/> is <c>EMFILE</c> or <c>ENFILE</c> on Unix and
    /// <c>WSAEMFILE</c> on Windows. <see cref="SocketError.NoBufferSpaceAvailable"/> is <c>ENOBUFS</c> or
    /// <c>WSAENOBUFS</c>.
    /// </para>
    /// <para>
    /// On Unix, <c>ENOMEM</c> and Linux's <c>ENOSR</c> have no <see cref="SocketError"/> of their own. They
    /// arrive as <see cref="SocketError.SocketError"/>, and so do <c>EPROTO</c> and <c>ENONET</c>, two of the
    /// pending network errors Linux reports for a queued connection. The value cannot tell them apart, so
    /// all four are backed off: that never spins while memory is short, and it costs a misclassified
    /// network error one short wait. For a stream listening socket, every error <c>accept(2)</c> documents
    /// that .NET reports this way is transient. Windows reports Winsock codes, each of which has a
    /// <see cref="SocketError"/>, so the generic value is not backed off there.
    /// </para>
    /// </remarks>
    internal static bool IsResourceExhaustion(SocketError error, bool isWindows, bool isStreamListener) => error switch
    {
        SocketError.TooManyOpenSockets or SocketError.NoBufferSpaceAvailable => true,
        SocketError.SocketError => !isWindows && isStreamListener,
        _ => false,
    };

    private static bool IsStream(Socket listenerSocket)
        => listenerSocket.SocketType == SocketType.Stream;
}
