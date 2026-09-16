using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using Assimalign.Cohesion.Hosting.Resources;
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
        string? gatewayName = null,
        ReadOnlyMemory<byte> applicationTrustKey = default,
        string applicationName = "appa",
        string resourceName = "identity",
        string environmentName = AppEnvironment.Keys.Local,
        ResourceMount? tlsCertificate = null)
    {
        endpoint ??= GetEndpoint();
        var mounts = new Dictionary<string, ResourceMount>
        {
            ["data"] = new ResourceMount(dataPath),
        };
        if (tlsCertificate is not null)
        {
            mounts.Add("tls", tlsCertificate);
        }

        var context = new ResourceContext(
            applicationName,
            resourceName,
            environmentName,
            gatewayName,
            dataPath,
            new Dictionary<string, Uri> { ["https"] = endpoint },
            mounts,
            settings: null,
            references: null,
            bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey,
            ambientValues: null);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        return IdentityHubApplication.CreateBuilder([], typeof(IdentityHubTestHost).Assembly);
    }
}
