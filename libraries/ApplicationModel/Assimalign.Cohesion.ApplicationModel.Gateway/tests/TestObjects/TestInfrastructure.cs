using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An in-memory platform: deploying a resource starts one instance whose control plane publishes to
/// the gateway, unless the resource is silent; every call is recorded.
/// </summary>
internal sealed class TestInfrastructure : IInfrastructureProvider
{
    public IResourceManifestPublisher? Gateway { get; set; }

    public HashSet<ResourceName> Silent { get; } = [];

    public ScaleAction Scale { get; } = new();

    public List<string> Deployed { get; } = [];

    public List<GatewayName> DeployedGateways { get; } = [];

    public List<ResourceName> TornDown { get; } = [];

    public Func<ApplicationResourceManifest, Task>? OnTeardown { get; set; }

    public ConcurrentDictionary<ResourceInstanceKey, (TestInstance Instance, ResourceControlPlane ControlPlane)> Instances { get; } = new();

    public IReadOnlyCollection<IApplicationResourceAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        return [Scale];
    }

    public async ValueTask DeployResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        Deployed.Add($"{model.Name}:{resource.Provider}/{resource.Name}");
        if (!Silent.Contains(resource.Name))
        {
            await StartInstanceAsync(resource.Provider, resource.Name, cancellationToken);
        }
    }

    public ValueTask DeployGatewayAsync(IApplicationModel model, GatewayName gateway, CancellationToken cancellationToken = default)
    {
        DeployedGateways.Add(gateway);
        return ValueTask.CompletedTask;
    }

    public async ValueTask TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        TornDown.Add(resource.Name);
        if (OnTeardown is not null)
        {
            await OnTeardown.Invoke(resource);
        }
    }

    /// <summary>Starts one instance, as a platform does on deployment or scale-out.</summary>
    public async Task<TestInstance> StartInstanceAsync(ProviderName provider, ResourceName name, CancellationToken cancellationToken = default)
    {
        var instance = new TestInstance(provider, name);
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(instance, Gateway!, TestSerializer.Json);
        await controlPlane.StartAsync(cancellationToken);
        Instances[instance.Key] = (instance, controlPlane);
        return instance;
    }
}
