using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

/// <summary>
/// A client that writes a request head to a proxy byte for byte, for the requests
/// <see cref="System.Net.Http.HttpClient"/> does not let a test write, such as a <c>CONNECT</c> whose
/// tunnel the test then drives.
/// </summary>
internal sealed class RawProxyClient : IAsyncDisposable
{
    private readonly Socket _socket;

    private RawProxyClient(Socket socket)
    {
        _socket = socket;
        Stream = new NetworkStream(socket, ownsSocket: false);
    }

    /// <summary>Gets the connection to the proxy.</summary>
    public NetworkStream Stream { get; }

    public static async Task<RawProxyClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
            return new RawProxyClient(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="head"/> and reads the proxy's response head.
    /// </summary>
    /// <returns>The status code and the head's text.</returns>
    public async Task<(int Status, string Head)> SendAsync(string head, CancellationToken cancellationToken)
    {
        await Stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        string response = await ReadHeadAsync(cancellationToken);
        int status = response.Length >= 12 ? int.Parse(response.AsSpan(9, 3), provider: null) : 0;
        return (status, response);
    }

    /// <summary>
    /// Writes <paramref name="text"/> and reads the same number of bytes back.
    /// </summary>
    public async Task<string> RoundTripAsync(string text, CancellationToken cancellationToken)
    {
        byte[] sent = Encoding.ASCII.GetBytes(text);
        await Stream.WriteAsync(sent, cancellationToken);

        byte[] received = new byte[sent.Length];
        await Stream.ReadExactlyAsync(received, cancellationToken);
        return Encoding.ASCII.GetString(received);
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _socket.Dispose();
    }

    private async Task<string> ReadHeadAsync(CancellationToken cancellationToken)
    {
        StringBuilder head = new();
        byte[] one = new byte[1];

        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await Stream.ReadAsync(one, cancellationToken) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }
}
