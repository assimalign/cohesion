using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal static class TestPorts
{
    // Below every platform's ephemeral range (Linux 32768, Windows and macOS 49152). A port from
    // bind(0) stays free for any other socket until the server binds it, so a parallel test's
    // listener could take it, and a test asserting that nothing accepts before its server starts
    // would read that listener. No connect() or bind(0) lands in this range.
    private const int firstPort = 20000;
    private const int portCount = 12000;
    private static int _next = Environment.ProcessId % portCount;

    public static int GetFree()
    {
        for (int attempt = 0; attempt < portCount; attempt++)
        {
            int port = firstPort + Interlocked.Increment(ref _next) % portCount;
            using var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return port;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Another process holds it, or Windows reserves it. Try the next one.
            }
        }

        throw new InvalidOperationException("No free test port below the ephemeral range.");
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
