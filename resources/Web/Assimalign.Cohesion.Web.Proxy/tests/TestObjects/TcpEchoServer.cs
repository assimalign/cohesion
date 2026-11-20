using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

/// <summary>
/// A plain TCP server on loopback that echoes every byte each connection sends until the peer finishes
/// sending: the destination of a <c>CONNECT</c> tunnel, which carries bytes rather than HTTP.
/// </summary>
internal sealed class TcpEchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly Task _accepting;
    private int _accepted;

    private TcpEchoServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    /// <summary>Gets the loopback port the server listens on.</summary>
    public int Port { get; }

    /// <summary>Gets how many connections the server accepted.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    public static TcpEchoServer Start()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new TcpEchoServer(listener);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();

        Task[] connections;
        lock (_connections)
        {
            connections = _connections.ToArray();
        }

        try
        {
            await Task.WhenAll([_accepting, .. connections]);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            // The server's own shutdown is not under test.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket = await _listener.AcceptSocketAsync(_stop.Token);
            Interlocked.Increment(ref _accepted);

            lock (_connections)
            {
                _connections.Add(EchoAsync(socket));
            }
        }
    }

    private async Task EchoAsync(Socket socket)
    {
        using (socket)
        {
            await using NetworkStream stream = new(socket, ownsSocket: false);
            byte[] buffer = new byte[1024];
            int read;

            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }

            socket.Shutdown(SocketShutdown.Send);
        }
    }
}
