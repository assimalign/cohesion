using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// One running instance of a resource, whose one action reports the environment it runs in.
/// </summary>
internal sealed class TestResource : IApplicationInstance
{
    public TestResource(ProviderName provider, ResourceName name, DeploymentId? deployment)
    {
        Provider = provider;
        Name = name;
        Deployment = deployment;
    }

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; } = [new EnvironmentAction()];

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors => [];

    /// <summary>Reads the resource name the environment the action runs in holds.</summary>
    private sealed class EnvironmentAction : IApplicationInstanceAction
    {
        public ActionName Name => "environment";

        public ApplicationActionKind Kind => ApplicationActionKind.Read;

        public IApplicationInstanceActionState? State => null;

        public ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
        {
            var environment = new ProgramEnvironment(AppEnvironment.GetValue(AppEnvironment.Variables.Resource));
            return ValueTask.FromResult(ApplicationActionResult.Success(environment, context.Serializer));
        }
    }
}

/// <summary>What an action read from the environment it ran in.</summary>
/// <param name="Resource">The resource name the environment holds, if any.</param>
public sealed record ProgramEnvironment(string? Resource);
