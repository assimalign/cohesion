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
/// The provider answers "where": its own configuration says where resources run, and it adds
/// <see cref="GatewayDeploymentManifest.Infrastructure"/> to the deployment manifest of each child
/// gateway it deploys. The gateway answers "what", from its model, and writes the rest of that
/// manifest.
/// </para>
/// <para>
/// The gateway reconciles: it plans each resource (<see cref="PlanResourceAsync"/>), deploys only
/// the ones whose plan changes something, and tears down the resources and child gateways the
/// deployment no longer declares. Workloads outlive the gateway, so a provider answers from what
/// the platform runs, not from what it was told earlier in the same process. A deployed resource's
/// instances publish their manifests to the gateway; the provider only starts them.
/// </para>
/// <para>
/// The gateway deploys in dependency order: a resource or child gateway once everything it depends
/// on is ready, and those that do not depend on each other at the same time. It plans a resource
/// at that point too, because the resource's settings may be filled in from what it depends on.
/// So the gateway may call the provider concurrently for different resources and child gateways,
/// but never concurrently for the same one.
/// </para>
/// <para>
/// A resource's settings are configuration values its instances receive with their deployment
/// (<see cref="ApplicationResourceManifest.Settings"/>). The gateway passes their values, with every
/// output filled in, to <see cref="PlanResourceAsync"/> and <see cref="DeployResourceAsync"/>; the
/// provider delivers them to the instances, for example as environment variables.
/// </para>
/// </remarks>
public interface IInfrastructureProvider
{
    /// <summary>
    /// Gets the platform the provider deploys onto, such as <c>kubernetes</c>.
    /// </summary>
    /// <remarks>
    /// A resource's platform extensions (<see cref="ApplicationResourceManifest.Extensions"/>) apply
    /// only on their platform. The gateway passes every method the resource as declared for this
    /// platform: its manifest with only the extensions for <see cref="Platform"/>, so the provider
    /// reads its own with its own JSON contract and never sees another platform's.
    /// </remarks>
    PlatformName Platform { get; }

    /// <summary>
    /// Gets the infrastructure actions the provider exposes on a resource, such as scaling it.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>, with only this
    /// platform's extensions.
    /// </param>
    /// <returns>The actions; empty for none.</returns>
    IReadOnlyCollection<IApplicationResourceAction> GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource);

    /// <summary>
    /// Plans a resource's deployment without changing anything: whether its workload must be created
    /// or updated, and the revision it would run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gateway plans a resource once every resource it depends on in this gateway's part is
    /// ready, so <paramref name="settings"/> holds the values the deployment would deliver. A
    /// workload that runs other values needs an update.
    /// </para>
    /// <para>
    /// The revision fingerprints everything the provider deploys for the resource that the model does
    /// not carry, such as its image and the provider's own configuration. It leaves out the settings'
    /// values and the resource's extensions: the gateway fingerprints the manifest as declared for
    /// this platform, extensions included, itself. The gateway compares the whole with the revision it
    /// recorded: a different one is a new definition, which starts without the state recorded for the
    /// previous one. A value filled in from a dependency's output changes when that dependency is
    /// redeployed, which is not a new definition of this resource.
    /// </para>
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>, with only this
    /// platform's extensions.
    /// </param>
    /// <param name="settings">
    /// The values of the resource's settings by name, with every output filled in; the manifest's
    /// <see cref="ApplicationResourceManifest.Settings"/> gives their order.
    /// </param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    /// <returns>What deploying the resource would change, and the revision it would run.</returns>
    ValueTask<InfrastructurePlan> PlanResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a resource's workload, as its plan said, and delivers its settings to the
    /// instances it starts.
    /// </summary>
    /// <remarks>
    /// The gateway calls it only when the plan's change is <see cref="InfrastructureChange.Create"/>
    /// or <see cref="InfrastructureChange.Update"/>, right after the plan, once every resource the
    /// resource depends on in this gateway's part is ready: an instance of it has published and its
    /// outputs are read. How an update replaces running instances, all at once or a few at a time, is
    /// the platform's rollout.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>, with only this
    /// platform's extensions.
    /// </param>
    /// <param name="settings">The values the plan was made with.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the workload has been started, not when it is ready.</returns>
    ValueTask DeployResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the values the platform gave a deployed resource, such as the endpoint of the load
    /// balancer or service in front of its instances.
    /// </summary>
    /// <remarks>
    /// The gateway reads a resource's outputs once an instance of its deployment has published, or,
    /// for a resource the plan left unchanged, right after the plan, and before it plans anything that
    /// depends on the resource. It records them on the resource and fills them in where a dependent's
    /// setting refers to them (<see cref="ApplicationResourceOutput"/>). Answer from what the platform
    /// runs: an output the platform assigns later, such as a load balancer's address, is read again on
    /// the next run.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>, with only this
    /// platform's extensions.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outputs by name; empty when the resource has none.</returns>
    ValueTask<IReadOnlyDictionary<OutputName, string>> GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a child gateway with its deployment manifest, or updates one already running; a child
    /// already running that manifest is left as it is.
    /// </summary>
    /// <remarks>
    /// The gateway writes the child's manifest: <see cref="IApplicationModel.Manifest"/>, so the child
    /// resolves dependencies outside its own part of the hierarchy; the child's name; and the values of
    /// the outputs from this gateway and those above that settings in the child's part use. The
    /// provider adds where the child runs (<see cref="GatewayDeploymentManifest.Infrastructure"/>),
    /// for example with <c>deployment with { Infrastructure = ... }</c>. The gateway calls it on every
    /// run, once the resources of this gateway that the child's part depends on are ready, so a
    /// changed output reaches the child as a changed manifest; the child reconciles its own part.
    /// </remarks>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="deployment">
    /// The child's deployment manifest, for one of the model's <see cref="IApplicationModel.Deployment"/>
    /// gateways, without <see cref="GatewayDeploymentManifest.Infrastructure"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>A task that completes when the child gateway has been started.</returns>
    ValueTask DeployGatewayAsync(IApplicationModel model, GatewayDeploymentManifest deployment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a resource's workload: after a new resource missed its startup window, or after the
    /// deployment stopped declaring it.
    /// </summary>
    /// <param name="model">The model the gateway runs.</param>
    /// <param name="resource">
    /// The resource, from the model's <see cref="IApplicationModel.Deployment"/>, with only this
    /// platform's extensions; for a resource the deployment removed, its provider and name as the
    /// gateway recorded them, with no dependencies.
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
