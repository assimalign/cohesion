using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;
// The shared ApplicationModel namespace also declares a ResourceMount; this file means the runtime one.
using ResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

internal static class ConfigurationStoreTestHost
{
    // Wraps the default control plane a builder created in this asynchronous flow, so a test can stand a
    // custom IResourceControlPlane in for it; the registration is per assembly and made once.
    private static readonly AsyncLocal<Func<IResourceControlPlane, IResourceControlPlane>?> _controlPlaneDecorator = new();

    /// <summary>
    /// Makes builders created in the current asynchronous flow use the control plane
    /// <paramref name="decorate"/> returns for the default one.
    /// </summary>
    internal static void DecorateControlPlane(Func<IResourceControlPlane, IResourceControlPlane> decorate)
    {
        _controlPlaneDecorator.Value = decorate;
    }

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
            static () => _controlPlaneDecorator.Value is { } decorate
                ? decorate(global::Assimalign.Cohesion.Hosting.Resources.ResourceControlPlane.Create([ "configurationstore.add-namespace", "configurationstore.set-value", "configurationstore.remove-value", ]))
                : global::Assimalign.Cohesion.Hosting.Resources.ResourceControlPlane.Create([ "configurationstore.add-namespace", "configurationstore.set-value", "configurationstore.remove-value", ]));
    }
}
