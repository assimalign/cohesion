using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c>, whose one action reports the environment it runs in.
/// </summary>
internal sealed class TestResource : IApplicationInstance
{
    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment => null;

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; } = [new EnvironmentAction()];

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors => [];

    public static IApplicationInstanceControlPlane CreateControlPlane()
    {
        return ApplicationInstanceControlPlane.Create(new TestResource(), JsonApplicationContentSerializer.Create(TestJsonContext.Default));
    }

    /// <summary>Reads the resource name the environment the action runs in holds.</summary>
    private sealed class EnvironmentAction : IApplicationInstanceAction
    {
        public ActionName Name => "environment";

        public ApplicationActionKind Kind => ApplicationActionKind.Read;

        public IApplicationInstanceActionState? State => null;

        public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
        {
            // Yield first, so the read happens on a continuation, as an action's work usually does.
            await Task.Yield();
            var environment = new ProgramEnvironment(AppEnvironment.GetValue(AppEnvironment.Variables.Resource));
            return ApplicationActionResult.Success(environment, context.Serializer);
        }
    }
}

