using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A gateway's deployment of one application resource: the instances running its definition and the
/// state written to them since the gateway was deployed.
/// </summary>
/// <remarks>
/// <para>
/// An instance registers when it publishes its manifest, and the answer is the state it must
/// restore before serving. After a stateful write succeeds on one instance, the gateway records
/// the write's snapshot; the answer is every other instance that must restore it. Registration and
/// recording are atomic with respect to each other, so every instance receives every recorded
/// snapshot exactly once: in its registration answer or through a restore.
/// </para>
/// <para>
/// The state lasts until the gateway is next deployed. Keeping it across a restart of the same
/// deployment is the gateway's job: it persists what it records and seeds a new aggregate with it.
/// Members are safe to call concurrently.
/// </para>
/// </remarks>
public interface IApplicationResourceDeployment : IApplicationResource
{
    /// <summary>
    /// Gets the resource as the deployment declared it.
    /// </summary>
    ApplicationResourceManifest Manifest { get; }

    /// <summary>
    /// Gets the registered instances' manifests, ordered by instance key and so by start time.
    /// </summary>
    IReadOnlyCollection<ApplicationInstanceManifest> Instances { get; }

    /// <summary>
    /// Gets the state recorded since the gateway was deployed: one snapshot per action, ordered by
    /// action name.
    /// </summary>
    IReadOnlyCollection<ApplicationActionSnapshot> State { get; }

    /// <summary>
    /// Gets the infrastructure actions the gateway exposes on the resource, ordered by name.
    /// </summary>
    IReadOnlyCollection<IApplicationResourceAction> Actions { get; }

    /// <summary>
    /// Gets the values the resource's platform reported once it was deployed, such as its endpoint;
    /// empty until the gateway has read them.
    /// </summary>
    IReadOnlyDictionary<OutputName, string> Outputs { get; }

    /// <summary>
    /// Executes one invocation of an infrastructure action.
    /// </summary>
    /// <param name="action">The action's name.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>
    /// The action's result, <see cref="ApplicationActionStatus.NotFound"/> when the resource has no such
    /// action, <see cref="ApplicationActionStatus.NotSupported"/> when the action does not accept the
    /// requested operation, or <see cref="ApplicationActionStatus.Rejected"/> when the input does not
    /// bind.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an instance and returns the state it must restore before serving.
    /// </summary>
    /// <remarks>
    /// Registering an instance that is already registered replaces its manifest, so a publication
    /// can be retried. The answer holds the recorded snapshot of each action the instance's manifest
    /// declares stateful.
    /// </remarks>
    /// <param name="instance">The manifest the instance published.</param>
    /// <returns>The snapshots to restore.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">
    /// The manifest names another resource, or has no instance key.
    /// </exception>
    IReadOnlyList<ApplicationActionSnapshot> Register(ApplicationInstanceManifest instance);

    /// <summary>
    /// Removes an instance that stopped.
    /// </summary>
    /// <param name="key">The instance's key.</param>
    /// <returns>Whether the instance was registered.</returns>
    bool Unregister(InstanceKey key);

    /// <summary>
    /// Records the snapshot a stateful write left on one instance, and returns the other instances
    /// that must restore it.
    /// </summary>
    /// <remarks>
    /// The snapshot replaces the action's previous one. The answer holds every other registered
    /// instance whose manifest declares the action stateful; restore the snapshot on each one rather
    /// than repeating the write.
    /// </remarks>
    /// <param name="source">The key of the instance the write ran on.</param>
    /// <param name="snapshot">The snapshot the write returned.</param>
    /// <returns>The keys of the instances that must restore the snapshot.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException"><paramref name="source"/> is not registered.</exception>
    /// <exception cref="System.ArgumentException">
    /// The source instance's manifest does not declare the snapshot's action stateful.
    /// </exception>
    IReadOnlyList<InstanceKey> Record(InstanceKey source, ApplicationActionSnapshot snapshot);
}
