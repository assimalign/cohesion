using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel.Tests;

internal static class TestPorts
{
    /// <summary>
    /// Returns a loopback port no listener holds right now.
    /// </summary>
    public static int GetFree()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
