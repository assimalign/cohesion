using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

internal static class SecretStoreTestHost
{
    internal static Uri GetEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }

    internal static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-secret-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Runs the current flow in the named environment. A SecretStore permits plaintext HTTP only on
    /// loopback in Local, and every test endpoint is plaintext loopback.
    /// </summary>
    internal static IDisposable EnterEnvironment(string environmentName = AppEnvironment.Keys.Local)
    {
        return AppEnvironment.Enter(AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Environment] = environmentName },
            CancellationToken.None));
    }

    internal static SecretStoreApplicationBuilder CreateBuilder()
        => SecretStoreApplication.CreateBuilder([]);

    internal static SecretStoreApplicationBuilder CreateBuilder(string dataPath)
        => CreateBuilder(GetEndpoint(), dataPath);

    internal static SecretStoreApplicationBuilder CreateBuilder(Uri endpoint, string dataPath)
        => SecretStoreApplication.CreateBuilder(["--endpoint", endpoint.AbsoluteUri, "--data", dataPath]);
}
