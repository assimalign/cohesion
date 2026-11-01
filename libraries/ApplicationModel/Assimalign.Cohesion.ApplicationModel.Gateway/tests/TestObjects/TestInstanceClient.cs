using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Calls the test platform's instances' control planes directly, the way the HTTP client calls a real
/// instance's: the content crosses as bytes and the control plane reads it with its own serializer.
/// An instance listed as unreachable throws, as one that stopped answering would, and one listed as
/// refusing rejects every restore. Every call is recorded.
/// </summary>
internal sealed class TestInstanceClient : IApplicationGatewayInstanceClient
{
    private readonly TestInfrastructure _infrastructure;

    public TestInstanceClient(TestInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public ConcurrentDictionary<InstanceKey, bool> Unreachable { get; } = new();

    public ConcurrentDictionary<InstanceKey, bool> Refusing { get; } = new();

    /// <summary>The calls made, as <c>execute:{key}</c> and <c>restore:{key}</c>.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Runs before each restore reaches its instance, such as a delay of a slow network.</summary>
    public Func<ApplicationActionSnapshot, Task>? BeforeRestore { get; set; }

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"execute:{instance.Key}");
        IApplicationInstanceControlPlane controlPlane = Find(instance);
        return controlPlane.ExecuteAsync(action, ApplicationActionContext.Create(context.Operation, controlPlane.Serializer, context.Input), cancellationToken);
    }

    public async ValueTask<ApplicationActionResult> RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"restore:{instance.Key}");
        if (BeforeRestore is { } before)
        {
            await before.Invoke(snapshot);
        }

        IApplicationInstanceControlPlane controlPlane = Find(instance);
        return Refusing.ContainsKey(instance.Key)
            ? ApplicationActionResult.Rejected("The snapshot does not bind.")
            : await controlPlane.RestoreAsync(snapshot, cancellationToken);
    }

    private IApplicationInstanceControlPlane Find(ApplicationInstanceManifest instance)
    {
        if (Unreachable.ContainsKey(instance.Key) || !_infrastructure.Instances.TryGetValue(instance.Key, out var running))
        {
            throw new HttpRequestException($"Instance {instance.Key} did not answer.");
        }

        return running.Service.ControlPlane;
    }
}
