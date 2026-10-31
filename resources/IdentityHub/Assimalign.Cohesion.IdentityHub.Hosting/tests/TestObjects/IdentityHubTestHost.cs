using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

internal static class IdentityHubTestHost
{
    internal static Uri GetEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }

    internal static IdentityHubApplicationBuilder CreateBuilder(
        string dataPath,
        Uri? endpoint = null,
        string environmentName = AppEnvironment.Keys.Local)
    {
        endpoint ??= GetEndpoint();

        // The builder reads the environment name when it is created, so a scope around its creation
        // supplies one without touching the process environment.
        AppEnvironmentScope scope = AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Environment] = environmentName },
            CancellationToken.None);
        using IDisposable entered = AppEnvironment.Enter(scope);
        return IdentityHubApplication.CreateBuilder(["--endpoint", endpoint.ToString(), "--data", dataPath]);
    }
}
