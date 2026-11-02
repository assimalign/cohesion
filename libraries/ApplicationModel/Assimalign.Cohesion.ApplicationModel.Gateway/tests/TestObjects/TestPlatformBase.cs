using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A platform as one object, the way a platform's gateway half is both the infrastructure and the
/// instance client: it forwards to an in-memory <see cref="TestInfrastructure"/> and its client, and
/// records its disposal with how often the gateway had stopped its channel by then.
/// </summary>
internal abstract class TestPlatformBase : IApplicationGatewayInfrastructureProvider, IApplicationGatewayInstanceClient
{
    private int _disposals;

    public TestInfrastructure Infrastructure { get; } = new();

    public TestInstanceClient Client => Infrastructure.Client;

    /// <summary>The platform's name; <see langword="default"/> makes a platform that names none.</summary>
    public PlatformName Platform { get; set; } = "test";

    /// <summary>How many times the platform was disposed.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <summary>How many times the gateway had stopped the channel when the platform was first disposed; -1 before.</summary>
    public int StopsAtDisposal { get; private set; } = -1;

    public ValueTask StartAsync(IApplicationGatewayContext context, CancellationToken cancellationToken = default)
    {
        return Infrastructure.StartAsync(context, cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        return Infrastructure.StopAsync(cancellationToken);
    }

    public IReadOnlyCollection<IApplicationGatewayDeploymentAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        return Infrastructure.GetResourceActions(model, resource);
    }

    public ValueTask<ApplicationGatewayDeploymentPlan> PlanResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, IReadOnlyDictionary<SettingName, string> settings, CancellationToken cancellationToken = default)
    {
        return Infrastructure.PlanResourceAsync(model, resource, settings, cancellationToken);
    }

    public ValueTask DeployResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, IReadOnlyDictionary<SettingName, string> settings, DeploymentId deployment, CancellationToken cancellationToken = default)
    {
        return Infrastructure.DeployResourceAsync(model, resource, settings, deployment, cancellationToken);
    }

    public ValueTask WatchResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, DeploymentId deployment, CancellationToken cancellationToken = default)
    {
        return Infrastructure.WatchResourceAsync(model, resource, deployment, cancellationToken);
    }

    public ValueTask<IReadOnlyDictionary<OutputName, string>> GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        return Infrastructure.GetResourceOutputsAsync(model, resource, cancellationToken);
    }

    public ValueTask TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default)
    {
        return Infrastructure.TeardownResourceAsync(model, resource, cancellationToken);
    }

    public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        return Client.ExecuteAsync(instance, action, context, cancellationToken);
    }

    public ValueTask<ApplicationActionResult> RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        return Client.RestoreAsync(instance, snapshot, cancellationToken);
    }

    protected void RecordDisposal()
    {
        if (Interlocked.Increment(ref _disposals) == 1)
        {
            StopsAtDisposal = Infrastructure.Stops;
        }
    }
}
