using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>
/// A loopback port nothing listens on. The socket is bound, so no other process takes the port
/// while a test runs, but it never listens, so the operating system refuses a dial to it.
/// </summary>
internal sealed class UnreachableEndPoint : IDisposable
{
    private readonly Socket _socket;

    public UnreachableEndPoint()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    /// <summary>Gets the bound endpoint.</summary>
    public IPEndPoint EndPoint { get; }

    public void Dispose() => _socket.Dispose();
}
