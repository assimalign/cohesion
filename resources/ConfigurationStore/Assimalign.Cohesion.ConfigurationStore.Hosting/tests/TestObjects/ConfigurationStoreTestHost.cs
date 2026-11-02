using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

internal static class ConfigurationStoreTestHost
{
    internal static Uri GetEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }

    internal static ConfigurationStoreApplicationBuilder CreateBuilder(Uri endpoint, string dataPath)
    {
        return ConfigurationStoreApplication.CreateBuilder(
            ["--endpoint", endpoint.AbsoluteUri, "--data", dataPath]);
    }
}
