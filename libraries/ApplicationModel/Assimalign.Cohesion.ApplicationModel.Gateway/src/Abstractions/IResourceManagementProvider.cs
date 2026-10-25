using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Serves the management API over a gateway's deployment records and its resources' instances: the operations behind
/// <c>/cohesion/v2/resources/{resource}/…</c>. A gateway control plane calls it after it has authenticated the caller
/// and checked the caller's management grants (<see cref="ResourceManagementOperations"/>).
/// </summary>
/// <remarks>
/// A write is a conditional edit of the current generation's record: a non-null expected version that no longer
/// matches is refused as <see cref="ResourceManagementStatus.VersionMoved"/>. The provider stores nothing else, so a
/// gateway restart loses nothing, and every write is reported to the application's telemetry sink.
/// </remarks>
public interface IResourceManagementProvider
{
    /// <summary>Reads a resource's runtime surface and every record layer with its provenance.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The configuration document, whose version is the record's.</returns>
    ValueTask<ResourceManagementResult> GetConfigurationAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);

    /// <summary>Reads what each instance reports it applied, and how many have converged on the current record.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The convergence document.</returns>
    ValueTask<ResourceManagementResult> GetConfigurationStatusAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);

    /// <summary>Changes a section's management override in the current generation's record.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="section">The section path.</param>
    /// <param name="kind">How the override changes.</param>
    /// <param name="patch">The UTF-8 JSON merge patch; ignored for <see cref="ResourceOverrideChangeKind.Remove"/>.</param>
    /// <param name="expectedVersion">The record version the caller read, or null to write unconditionally.</param>
    /// <param name="caller">The authenticated caller, which the management event names.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The new override, generation and version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caller"/> is null.</exception>
    ValueTask<ResourceManagementResult> ChangeOverrideAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceSectionPath section,
        ResourceOverrideChangeKind kind,
        ReadOnlyMemory<byte> patch,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces an application's whole set of claims on a resource; an empty set withdraws them.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="owner">The claiming application.</param>
    /// <param name="entries">
    /// The UTF-8 JSON claim set: each member a claimable section's path, whose value is an object of keyed entries.
    /// Empty content or <c>{}</c> withdraws the owner's claims.
    /// </param>
    /// <param name="expectedVersion">The record version the caller read, or null to write unconditionally.</param>
    /// <param name="caller">The authenticated caller, which the management event names.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The new generation and version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caller"/> is null.</exception>
    ValueTask<ResourceManagementResult> ReplaceClaimsAsync(
        ApplicationName application,
        ResourceName resource,
        ApplicationName owner,
        ReadOnlyMemory<byte> entries,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an action on one instance or on every instance, as the action declares; nothing is stored.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="action">The action name.</param>
    /// <param name="input">The UTF-8 JSON input; empty content is JSON null.</param>
    /// <param name="caller">The authenticated caller, which the management event names.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>Each instance's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> or <paramref name="caller"/> is null.</exception>
    ValueTask<ResourceManagementResult> InvokeActionAsync(
        ApplicationName application,
        ResourceName resource,
        string action,
        ReadOnlyMemory<byte> input,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the platform's instance inventory for a resource, for diagnostics.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The inventory document.</returns>
    ValueTask<ResourceManagementResult> GetInstanceInventoryAsync(
        ApplicationName application,
        ResourceName resource,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a surface the platform publishes for a resource, such as <c>kubernetes/hpa</c>.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="surface">The platform-qualified surface key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The surface document.</returns>
    ValueTask<ResourceManagementResult> GetPlatformSurfaceAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceExtensionKey surface,
        CancellationToken cancellationToken = default);

    /// <summary>Changes a surface the platform publishes for a resource with a merge patch.</summary>
    /// <param name="application">The resource's application.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="surface">The platform-qualified surface key.</param>
    /// <param name="patch">The UTF-8 JSON merge patch.</param>
    /// <param name="expectedVersion">The version the caller read, or null to write unconditionally.</param>
    /// <param name="caller">The authenticated caller, which the management event names.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The changed surface document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caller"/> is null.</exception>
    ValueTask<ResourceManagementResult> ChangePlatformSurfaceAsync(
        ApplicationName application,
        ResourceName resource,
        ResourceExtensionKey surface,
        ReadOnlyMemory<byte> patch,
        string? expectedVersion,
        ApplicationCaller caller,
        CancellationToken cancellationToken = default);
}
