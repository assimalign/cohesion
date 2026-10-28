using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An in-memory platform: deploying a resource starts one instance whose control plane publishes to
/// the gateway, unless the resource is silent; every call is recorded. Its workloads outlive the
/// gateway that deployed them, so a second gateway over the same platform finds them running.
/// </summary>
internal sealed class TestInfrastructure : IInfrastructureProvider
{
    public IResourceManifestPublisher? Gateway { get; set; }

    public HashSet<ResourceName> Silent { get; } = [];

    public ScaleAction Scale { get; } = new();

    public List<string> Deployed { get; } = [];

    public List<GatewayName> DeployedGateways { get; } = [];

    public List<GatewayName> TornDownGateways { get; } = [];

    public List<ResourceName> TornDown { get; } = [];

    public Func<ApplicationResourceManifest, Task>? OnTeardown { get; set; }

    /// <summary>The revision the next deployment builds for each resource; "1" unless a test changes it.</summary>
    public Dictionary<ResourceName, string> Revisions { get; } = [];

    /// <summary>The workloads the platform runs, with the revision each runs.</summary>
    public ConcurrentDictionary<ResourceName, string> Running { get; } = new();

    public ConcurrentDictionary<ResourceInstanceKey, (TestInstance Instance, ResourceControlPlane ControlPlane)> Instances { get; } = new();

    public IReadOnlyCollection<IApplicationResourceAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        return [Scale];
    }

    public ValueTask<InfrastructurePlan> PlanResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        string revision = Revisions.GetValueOrDefault(resource.Name, "1");
        InfrastructureChange change = !Running.TryGetValue(resource.Name, out string? running) ? InfrastructureChange.Create
            : running == revision ? InfrastructureChange.None
            : InfrastructureChange.Update;

        return ValueTask.FromResult(new InfrastructurePlan(change, revision));
    }

    public async ValueTask DeployResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        Deployed.Add($"{model.Name}:{resource.Provider}/{resource.Name}");
        Running[resource.Name] = Revisions.GetValueOrDefault(resource.Name, "1");
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
        Running.TryRemove(resource.Name, out _);
        if (OnTeardown is not null)
        {
            await OnTeardown.Invoke(resource);
        }
    }

    public ValueTask TeardownGatewayAsync(IApplicationModel model, GatewayName gateway, CancellationToken cancellationToken = default)
    {
        TornDownGateways.Add(gateway);
        return ValueTask.CompletedTask;
    }

    /// <summary>Starts one instance, as a platform does on deployment or scale-out.</summary>
    public async Task<TestInstance> StartInstanceAsync(ProviderName provider, ResourceName name, CancellationToken cancellationToken = default)
    {
        var instance = new TestInstance(provider, name);
        ResourceControlPlane controlPlane = ResourceControlPlane.Create(instance, new GatewayForwarder(this), TestSerializer.Json);
        await controlPlane.StartAsync(cancellationToken);
        Instances[instance.Key] = (instance, controlPlane);
        return instance;
    }
}
