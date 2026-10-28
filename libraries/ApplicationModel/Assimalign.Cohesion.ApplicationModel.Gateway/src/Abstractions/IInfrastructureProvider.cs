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
/// gateway answers "what", from its model.
/// </para>
/// <para>
/// The gateway reconciles: it plans every resource first (<see cref="PlanResourceAsync"/>), deploys
/// only the ones whose plan changes something, and tears down the resources and child gateways the
/// deployment no longer declares. Workloads outlive the gateway, so a provider answers from what
/// the platform runs, not from what it was told earlier in the same process. A deployed resource's
/// instances publish their manifests to the gateway; the provider only starts them.
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
    /// Plans a resource's deployment without changing anything: whether its workload must be created
    /// or updated, and the revision it would run.
    /// </summary>
    /// <remarks>
    /// The revision fingerprints everything the provider deploys for the resource that the model does
    /// not carry, such as its image and platform settings. The gateway compares it with the revision
    /// it recorded: a different one is a new definition, which starts without the state recorded for
    /// the previous one.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    /// <returns>What deploying the resource would change, and the revision it would run.</returns>
    ValueTask<InfrastructurePlan> PlanResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a resource's workload, as its plan said.
    /// </summary>
    /// <remarks>
    /// The gateway calls it only when the plan's change is <see cref="InfrastructureChange.Create"/>
    /// or <see cref="InfrastructureChange.Update"/>. How an update replaces running instances, all at
    /// once or a few at a time, is the platform's rollout.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the workload has been started, not when it is ready.</returns>
    ValueTask DeployResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the values the platform gave a deployed resource, such as the endpoint of the load
    /// balancer or service in front of its instances.
    /// </summary>
    /// <remarks>
    /// The gateway reads every resource's outputs once its startup completes, records them on the
    /// resource, and fills them in where a dependent resource refers to them
    /// (<see cref="ApplicationResourceOutput"/>). Answer from what the platform runs: an output the
    /// platform assigns later, such as a load balancer's address, is read again on the next run.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">The resource, from the model's <see cref="IApplicationModel.Deployment"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outputs by name; empty when the resource has none.</returns>
    ValueTask<IReadOnlyDictionary<OutputName, string>> GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a child gateway with its deployment manifest, or updates one already running; a child
    /// already running that manifest is left as it is.
    /// </summary>
    /// <remarks>
    /// The child's manifest is <see cref="IApplicationModel.Manifest"/> with
    /// <see cref="GatewayDeploymentManifest.Gateway"/> set to <paramref name="gateway"/>, so the child
    /// resolves dependencies outside its own part of the hierarchy. The provider adds where the
    /// child runs. The gateway calls it on every run; the child reconciles its own part.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="gateway">The child gateway, one of the model's <see cref="IApplicationModel.Deployment"/> gateways.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the child gateway has been started.</returns>
    ValueTask DeployGatewayAsync(IApplicationModel model, GatewayName gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a resource's workload: after a new resource missed its startup window, or after the
    /// deployment stopped declaring it.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>; for a resource the
    /// deployment removed, its provider and name as the gateway recorded them, with no dependencies.
    /// </param>
    /// <param name="cancellationToken">Cancels the teardown.</param>
    /// <returns>A task that completes when the workload has been removed.</returns>
    ValueTask TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a child gateway the deployment no longer declares, with what it deployed.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="gateway">The child gateway the gateway recorded deploying.</param>
    /// <param name="cancellationToken">Cancels the teardown.</param>
    /// <returns>A task that completes when the child gateway has been removed.</returns>
    ValueTask TeardownGatewayAsync(IApplicationModel model, GatewayName gateway, CancellationToken cancellationToken = default);
}
