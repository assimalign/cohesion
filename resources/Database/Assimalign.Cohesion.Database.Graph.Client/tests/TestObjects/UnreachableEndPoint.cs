using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// A loopback port nothing listens on, so the operating system refuses a dial to it.
/// </summary>
/// <remarks>
/// The port comes from binding a socket to port 0, and the socket is closed at once. A bound socket
/// that never listens would keep other processes off the port, but it does not refuse a dial
/// everywhere: Linux and Windows answer the SYN with a reset, while macOS's BSD stack drops it, so
/// the dial hangs until the caller's token fires (CI, macOS, run 37632250844). With the port free,
/// every stack resets the SYN. Another process could take the port in between; an ephemeral port
/// is not reused that quickly.
/// </remarks>
internal sealed class UnreachableEndPoint : IDisposable
{
    public UnreachableEndPoint()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    /// <summary>Gets the endpoint.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>Does nothing: the port's socket was closed when the endpoint was chosen.</summary>
    public void Dispose()
    {
    }
}