using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;

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

    internal static ResourceContext CreateContext(
        Uri endpoint,
        string dataPath,
        string? bootstrapCredential = null,
        ReadOnlyMemory<byte> applicationTrustKey = default,
        string? gatewayName = "local",
        string applicationName = "appa",
        string resourceName = "secrets")
    {
        return new ResourceContext(
            applicationName,
            resourceName,
            "Development",
            gatewayName,
            dataPath,
            new Dictionary<string, Uri> { ["api"] = endpoint },
            new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(dataPath),
            },
            settings: null,
            references: null,
            bootstrapCredential is null
                ? ReadOnlyMemory<byte>.Empty
                : Encoding.ASCII.GetBytes(bootstrapCredential),
            applicationTrustKey,
            ambientValues: null);
    }

    internal static ISecretStoreApplicationBuilder CreateBuilder()
        => SecretStoreApplication.CreateBuilder([], typeof(SecretStoreTestHost).Assembly);
}
