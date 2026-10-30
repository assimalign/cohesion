using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An in-memory platform: deploying a resource starts one instance whose control plane publishes to
/// the gateway, unless the resource is silent; every call is recorded. Its workloads outlive the
/// gateway that deployed them, so a second gateway over the same platform finds them running.
/// </summary>
internal sealed class TestInfrastructure : IInfrastructureProvider
{
    private readonly Lock _calls = new();
    private readonly List<string> _events = [];

    public IApplicationInstancePublisher? Gateway { get; set; }

    public HashSet<ResourceName> Silent { get; } = [];

    /// <summary>
    /// Resources whose instance starts only once the task the function returns completes; their
    /// deployment returns at once, as a platform's does before the workload is ready.
    /// </summary>
    public ConcurrentDictionary<ResourceName, Func<Task>> Delayed { get; } = new();

    public ScaleAction Scale { get; } = new();

    public List<string> Deployed { get; } = [];

    public List<ResourceName> TornDown { get; } = [];

    public Func<ApplicationResourceManifest, Task>? OnTeardown { get; set; }

    /// <summary>The revision the next deployment builds for each resource; "1" unless a test changes it.</summary>
    public Dictionary<ResourceName, string> Revisions { get; } = [];

    /// <summary>The workloads the platform runs, with the revision each runs.</summary>
    public ConcurrentDictionary<ResourceName, string> Running { get; } = new();

    /// <summary>The settings each workload runs with, which its instances receive when they start.</summary>
    public ConcurrentDictionary<ResourceName, IReadOnlyDictionary<SettingName, string>> RunningSettings { get; } = new();

    /// <summary>The deployment each workload runs, which its instances receive when they start.</summary>
    public ConcurrentDictionary<ResourceName, DeploymentId> RunningDeployments { get; } = new();

    /// <summary>Every deployment the gateway made, in order, with the resource it deployed.</summary>
    public ConcurrentQueue<(ResourceName Resource, DeploymentId Deployment)> Deployments { get; } = new();

    /// <summary>The settings each resource was last planned with.</summary>
    public ConcurrentDictionary<ResourceName, IReadOnlyDictionary<SettingName, string>> Planned { get; } = new();

    /// <summary>The extensions each workload runs with, as <see cref="Describe"/> writes them.</summary>
    public ConcurrentDictionary<ResourceName, string> RunningExtensions { get; } = new();

    /// <summary>The manifest of each resource as the gateway last handed it to the platform.</summary>
    public ConcurrentDictionary<ResourceName, ApplicationResourceManifest> Seen { get; } = new();

    /// <summary>The platform's name; a resource's extensions for it apply here.</summary>
    public PlatformName Platform => "test";

    public ConcurrentDictionary<InstanceKey, (TestInstance Instance, ApplicationInstanceService Service)> Instances { get; } = new();

    /// <summary>
    /// The outputs the platform reports for each resource; an endpoint named after the resource and
    /// its gateway unless a test sets other values.
    /// </summary>
    public ConcurrentDictionary<ResourceName, IReadOnlyDictionary<OutputName, string>> Outputs { get; } = new();

    public ValueTask<IReadOnlyDictionary<OutputName, string>> GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        Record($"outputs:{resource.Name}");
        IReadOnlyDictionary<OutputName, string> outputs = Outputs.TryGetValue(resource.Name, out IReadOnlyDictionary<OutputName, string>? configured)
            ? configured
            : new Dictionary<OutputName, string> { [OutputName.Endpoint] = $"http://{resource.Name}.{model.Name}.svc" };

        return ValueTask.FromResult(outputs);
    }

    public IReadOnlyCollection<IApplicationResourceAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        Seen[resource.Name] = resource;
        return [Scale];
    }

    public ValueTask<InfrastructurePlan> PlanResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        CancellationToken cancellationToken = default)
    {
        Record($"plan:{resource.Name}");
        Planned[resource.Name] = settings;
        Seen[resource.Name] = resource;

        // A workload that runs other settings or extensions needs an update; the revision leaves
        // both out.
        string revision = Revisions.GetValueOrDefault(resource.Name, "1");
        InfrastructureChange change = !Running.TryGetValue(resource.Name, out string? running) ? InfrastructureChange.Create
            : running == revision
                && SameSettings(RunningSettings.GetValueOrDefault(resource.Name), settings)
                && RunningExtensions.GetValueOrDefault(resource.Name, string.Empty) == Describe(resource.Extensions) ? InfrastructureChange.None
            : InfrastructureChange.Update;

        return ValueTask.FromResult(new InfrastructurePlan(change, revision));
    }

    public async ValueTask DeployResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        CancellationToken cancellationToken = default)
    {
        Record(Deployed, $"{model.Name}:{resource.Provider}/{resource.Name}");
        Record($"deploy:{resource.Name}");
        Deployments.Enqueue((resource.Name, deployment));
        RunningDeployments[resource.Name] = deployment;
        RunningSettings[resource.Name] = settings;
        RunningExtensions[resource.Name] = Describe(resource.Extensions);
        Seen[resource.Name] = resource;
        Running[resource.Name] = Revisions.GetValueOrDefault(resource.Name, "1");
        if (Silent.Contains(resource.Name))
        {
            return;
        }

        if (Delayed.TryGetValue(resource.Name, out Func<Task>? delay))
        {
            _ = StartLaterAsync(resource, delay);
            return;
        }

        await StartInstanceAsync(resource.Provider, resource.Name, cancellationToken);
    }

    public async ValueTask TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        Record(TornDown, resource.Name);
        Record($"teardown:{resource.Name}");
        Running.TryRemove(resource.Name, out _);
        if (OnTeardown is not null)
        {
            await OnTeardown.Invoke(resource);
        }
    }

    /// <summary>
    /// The calls in the order they were made: <c>plan:</c>, <c>deploy:</c>, <c>outputs:</c> and
    /// <c>teardown:</c> with a resource name.
    /// </summary>
    public string[] Events()
    {
        lock (_calls)
        {
            return [.. _events];
        }
    }

    /// <summary>The position of a call in <see cref="Events"/>, or -1 when it was not made.</summary>
    public int IndexOf(string call)
    {
        return Array.IndexOf(Events(), call);
    }

    /// <summary>
    /// Starts one instance with the settings and the deployment its workload runs with, as a platform
    /// does on deployment or scale-out.
    /// </summary>
    public Task<TestInstance> StartInstanceAsync(ProviderName provider, ResourceName name, CancellationToken cancellationToken = default)
    {
        return StartInstanceAsync(provider, name, RunningDeployments.TryGetValue(name, out DeploymentId deployment) ? deployment : null, cancellationToken);
    }

    /// <summary>
    /// Starts one instance that repeats the given deployment, such as an instance an earlier rollout
    /// left running that restarts while a newer deployment rolls out.
    /// </summary>
    public async Task<TestInstance> StartInstanceAsync(ProviderName provider, ResourceName name, DeploymentId? deployment, CancellationToken cancellationToken = default)
    {
        var instance = new TestInstance(provider, name, RunningSettings.GetValueOrDefault(name) ?? new Dictionary<SettingName, string>(), deployment);
        ApplicationInstanceService service = ApplicationInstanceService.Create(ApplicationInstanceControlPlane.Create(instance, TestSerializer.Json), new GatewayForwarder(this));
        await service.StartingAsync(cancellationToken);
        Instances[instance.Key] = (instance, service);
        return instance;
    }

    // The gateway deploys independent resources at the same time, so every call is recorded under
    // one lock.
    private void Record<T>(List<T> calls, T call)
    {
        lock (_calls)
        {
            calls.Add(call);
        }
    }

    private void Record(string call)
    {
        Record(_events, call);
    }

    /// <summary>The extensions as text a platform can compare: each platform, name, and JSON value.</summary>
    public static string Describe(IReadOnlyList<ApplicationResourceExtension> extensions)
    {
        var text = new List<string>(extensions.Count);
        foreach (ApplicationResourceExtension extension in extensions)
        {
            text.Add($"{extension.Platform}/{extension.Name}={extension.Value.GetRawText()}");
        }

        return string.Join(";", text);
    }

    private static bool SameSettings(IReadOnlyDictionary<SettingName, string>? running, IReadOnlyDictionary<SettingName, string> planned)
    {
        if ((running?.Count ?? 0) != planned.Count)
        {
            return false;
        }

        foreach (KeyValuePair<SettingName, string> setting in planned)
        {
            if (!running!.TryGetValue(setting.Key, out string? value) || value != setting.Value)
            {
                return false;
            }
        }

        return true;
    }

    private async Task StartLaterAsync(ApplicationResourceManifest resource, Func<Task> delay)
    {
        await delay.Invoke();
        try
        {
            await StartInstanceAsync(resource.Provider, resource.Name);
        }
        catch (ApplicationGatewayException)
        {
            // The gateway stopped waiting for the resource, or stopped running.
        }
    }
}
