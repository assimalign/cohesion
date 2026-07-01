using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The controller that realizes an <see cref="IExecutableResource"/> as a supervised child
/// process. It applies (starts) and returns; readiness and exit are observed by the supervisor.
/// </summary>
internal sealed class LocalProcessController : IApplicationResourceController
{
    private readonly LocalGatewayProcessSupervisor _supervisor;

    public LocalProcessController(LocalGatewayProcessSupervisor supervisor)
    {
        _supervisor = supervisor;
    }

    public bool CanControl(IApplicationResource resource) => resource is IExecutableResource;

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        var resource = (IExecutableResource)context.Resource;
        IExecutableArtifact artifact = context.GetArtifact<IExecutableArtifact>();

        _supervisor.Start(context.Resource, artifact, resource.EnvironmentVariables);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
        => _supervisor.StopAsync(context.Resource, cancellationToken);
}
