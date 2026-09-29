using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.ConfigurationStore.Hosting;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

// The shared ApplicationModel namespace also declares a ResourceMount; this file means the runtime one.
using ResourceMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// A real, gateway-managed ConfigurationStore host on a loopback HTTP endpoint. It verifies the
/// ES256 bootstrap credential every request presents against <see cref="Identity"/>'s trust key, so
/// a provider read exercises the store's actual routes, authentication, and JSON format.
/// </summary>
/// <remarks>
/// <see cref="Create"/> installs the resource scope in the calling flow and must be called
/// synchronously from the test method; dispose the instance from that same method.
/// </remarks>
internal sealed class RunningConfigurationStore : IAsyncDisposable
{
    internal const string StoreResourceName = "configuration";

    private static readonly object _registrationGate = new();
    private static bool _controlPlaneRegistered;

    private readonly IDisposable _scope;
    private readonly ConfigurationStoreApplication _application;
    private readonly string _dataPath;
    private bool _started;

    private RunningConfigurationStore(
        IDisposable scope,
        ConfigurationStoreApplication application,
        TestBootstrapIdentity identity,
        Uri endpoint,
        string dataPath)
    {
        _scope = scope;
        _application = application;
        Identity = identity;
        Endpoint = endpoint;
        _dataPath = dataPath;
    }

    /// <summary>Gets the store's observed <c>api</c> endpoint (its control-plane endpoint).</summary>
    internal Uri Endpoint { get; }

    /// <summary>Gets the application trust key the store verifies credentials against.</summary>
    internal TestBootstrapIdentity Identity { get; }

    /// <summary>
    /// Gets the store's control-plane address as a gateway hands it to a provider: the observed
    /// endpoint combined with the manifest control-plane path <c>/cohesion/v1</c>.
    /// </summary>
    internal Uri ControlPlaneAddress => new(Endpoint, "/cohesion/v1");

    internal static RunningConfigurationStore Create(Action<ConfigurationStoreApplicationBuilder> configure)
    {
        EnsureControlPlaneRegistered();

        string dataPath = Path.Combine(
            Path.GetTempPath(),
            "cohesion-configuration-store-orchestration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        var identity = new TestBootstrapIdentity();
        Uri endpoint = GetEndpoint();
        var context = new ResourceContext(
            applicationName: "appa",
            resourceName: StoreResourceName,
            environmentName: "development",
            gatewayName: "local",
            contentRootPath: dataPath,
            endpoints: new Dictionary<string, Uri> { ["api"] = endpoint },
            mounts: new Dictionary<string, ResourceMount>
            {
                ["data"] = new ResourceMount(dataPath),
            },
            settings: null,
            references: null,
            bootstrapCredential: Encoding.ASCII.GetBytes(identity.Issue(StoreResourceName)),
            applicationTrustKey: identity.PublicKey,
            ambientValues: null);

        IDisposable scope = ResourceRuntime.CreateScope(context);
        bool created = false;
        try
        {
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder([]);
            configure.Invoke(builder);
            ConfigurationStoreApplication application = builder.Build();
            created = true;
            return new RunningConfigurationStore(scope, application, identity, endpoint, dataPath);
        }
        finally
        {
            if (!created)
            {
                scope.Dispose();
                identity.Dispose();
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    internal async Task StartAsync()
    {
        await ((IHost)_application).StartAsync().ConfigureAwait(false);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_started)
            {
                await ((IHost)_application).StopAsync().ConfigureAwait(false);
            }

            await ((IAsyncDisposable)_application).DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _scope.Dispose();
            Identity.Dispose();
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static Uri GetEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    }

    private static void EnsureControlPlaneRegistered()
    {
        lock (_registrationGate)
        {
            if (_controlPlaneRegistered)
            {
                return;
            }

            // ConfigurationStoreApplication.CreateBuilder(args) resolves the default control plane of
            // the process entry assembly (the test host). Registering one there makes the store
            // gateway-managed, so it authenticates every namespace read.
            Assembly entry = Assembly.GetEntryAssembly() ?? typeof(ConfigurationStoreApplication).Assembly;
            ResourceRuntime.RegisterControlPlane(
                entry,
                static () => ResourceControlPlane.Create(
                [
                    "configurationstore.add-namespace",
                    "configurationstore.set-value",
                    "configurationstore.remove-value",
                ]));
            _controlPlaneRegistered = true;
        }
    }
}
