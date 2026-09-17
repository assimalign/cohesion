using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

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

    internal static ResourceContext CreateContext(
        Uri endpoint,
        string dataPath,
        string bootstrapCredential,
        ReadOnlyMemory<byte> applicationTrustKey,
        string? gatewayName = "local")
    {
        return new ResourceContext(
            applicationName: "appa",
            resourceName: "configuration",
            environmentName: "development",
            gatewayName: gatewayName,
            contentRootPath: dataPath,
            endpoints: new Dictionary<string, Uri> { ["api"] = endpoint },
            mounts: new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(dataPath),
            },
            settings: null,
            references: null,
            bootstrapCredential: Encoding.ASCII.GetBytes(bootstrapCredential),
            applicationTrustKey: applicationTrustKey,
            ambientValues: null);
    }

    internal static ConfigurationStoreApplicationBuilder CreateBuilder()
    {
        return ConfigurationStoreApplication.CreateBuilder(
            [],
            typeof(ConfigurationStoreTestHost).Assembly);
    }

    [ModuleInitializer]
    internal static void RegisterControlPlane()
    {
        Assembly assembly = typeof(ConfigurationStoreTestHost).Assembly;
        ResourceRuntime.RegisterEntry(assembly);
        ResourceRuntime.RegisterControlPlane(
            assembly,
            static () => ConfigurationStoreResourceControlPlane.Create());
    }
}
