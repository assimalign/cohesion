using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The infrastructure a gateway deploys onto, such as Kubernetes, Docker, or the local machine.
/// It is the third layer an application brings together, beside the tenant and its identity
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// The provider answers "where": its own configuration says where resources run, and it writes
/// <see cref="GatewayDeploymentManifest.Infrastructure"/> for each child gateway it deploys. The
/// gateway answers "what", from its model, and calls each member below once per run.
/// </para>
/// <para>
/// A deployed resource's instances publish their manifests to the gateway; the provider only
/// starts them. A resource that publishes nothing within the gateway's startup window is torn down
/// through <see cref="TeardownResourceAsync"/>.
/// </para>
/// </remarks>
public interface IInfrastructureProvider
{
    /// <summary>
    /// Gets the infrastructure actions the provider exposes on a resource, such as scaling it.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <returns>The actions; empty for none.</returns>
    IReadOnlyCollection<IApplicationResourceAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource);

    /// <summary>
    /// Starts a resource's workload.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the workload has been started, not when it is ready.</returns>
    ValueTask DeployResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a child gateway with its deployment manifest.
    /// </summary>
    /// <remarks>
    /// The child's manifest is <see cref="IApplicationModel.Manifest"/> with
    /// <see cref="GatewayDeploymentManifest.Gateway"/> set to <paramref name="gateway"/>, so the child
    /// resolves dependencies outside its own part of the hierarchy. The provider adds where the
    /// child runs.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="gateway">The child gateway, one of the model's <see cref="IApplicationModel.Deployment"/> gateways.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the child gateway has been started.</returns>
    ValueTask DeployGatewayAsync(IApplicationModel model, GatewayName gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a resource's workload, after the resource missed its startup window.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <param name="cancellationToken">Cancels the teardown.</param>
    /// <returns>A task that completes when the workload has been removed.</returns>
    ValueTask TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);
}
