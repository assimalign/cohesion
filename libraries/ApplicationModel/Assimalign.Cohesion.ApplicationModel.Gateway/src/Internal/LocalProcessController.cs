using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The controller that realizes an <see cref="IExecutableResource"/> as a supervised child
/// process. It applies (starts) and returns; readiness and exit are observed by the supervisor.
/// </summary>
internal sealed class LocalProcessController : IApplicationResourceController
{
    private readonly LocalResourcePreparer _preparer;
    private readonly LocalGatewayProcessSupervisor _supervisor;

    public LocalProcessController(
        LocalResourcePreparer preparer,
        LocalGatewayProcessSupervisor supervisor)
    {
        _preparer = preparer;
        _supervisor = supervisor;
    }

    public bool CanControl(IApplicationResource resource) => resource is IExecutableResource;

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        IExecutableArtifact artifact = context.GetArtifact<IExecutableArtifact>();
        LocalResourceConfiguration configuration = await _preparer
            .PrepareAsync(context, artifact, cancellationToken)
            .ConfigureAwait(false);
        _supervisor.Start(configuration);
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
        => _supervisor.StopAsync(context.Resource, cancellationToken);
}
