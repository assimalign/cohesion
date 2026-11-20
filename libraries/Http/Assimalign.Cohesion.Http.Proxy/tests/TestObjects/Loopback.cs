using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// Real loopback sockets for the tests that need a connection to switch protocols or to tunnel:
/// a connected socket pair standing in for the client's connection, and a one-shot upstream server.
/// </summary>
internal static class Loopback
{
    /// <summary>
    /// Creates two connected TCP streams: the server end the proxy relays on, and the client end the
    /// test drives.
    /// </summary>
    public static async Task<(NetworkStream Server, NetworkStream Client)> CreateSocketPairAsync()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connect = client.ConnectAsync(listener.LocalEndpoint);
        Socket server = await listener.AcceptSocketAsync();
        await connect;

        return (new NetworkStream(server, ownsSocket: true), new NetworkStream(client, ownsSocket: true));
    }

    /// <summary>
    /// Starts a server that accepts one connection, reads its request head, and hands the head and the
    /// connection to <paramref name="handler"/>.
    /// </summary>
    public static LoopbackServer Serve(Func<string, NetworkStream, Task> handler)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackServer(listener, handler);
    }

    /// <summary>
    /// Returns a port nothing listens on.
    /// </summary>
    public static int GetUnusedPort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes as ASCII text.
    /// </summary>
    public static async Task<string> ReadTextAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>
    /// Echoes everything it reads until the peer finishes sending, then closes.
    /// </summary>
    public static async Task EchoAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        stream.Socket.Shutdown(SocketShutdown.Send);
    }

    internal static async Task<string> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder head = new();
        byte[] one = new byte[1];

        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }
}

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _serving;

    public LoopbackServer(TcpListener listener, Func<string, NetworkStream, Task> handler)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _serving = ServeAsync(handler);
    }

    public int Port { get; }

    public string? RequestHead { get; private set; }

    public Uri Uri => new($"http://127.0.0.1:{Port}/");

    /// <summary>
    /// Waits for the server to finish with its connection, surfacing a failure the handler hit.
    /// </summary>
    public Task Completion => _serving;

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _stop.Cancel();

        try
        {
            await _serving;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            // The test already observed what it needed; the server's own shutdown is not under test.
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(Func<string, NetworkStream, Task> handler)
    {
        using Socket socket = await _listener.AcceptSocketAsync(_stop.Token);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        RequestHead = await Loopback.ReadHeadAsync(stream, _stop.Token);
        await handler(RequestHead, stream);
    }
}
