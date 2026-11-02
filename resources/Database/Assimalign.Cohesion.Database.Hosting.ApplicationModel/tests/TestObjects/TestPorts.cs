using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal static class TestPorts
{
    public static int GetFree()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task<bool> AcceptsAsync(int port, CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return false;
        }
    }
}
