using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Defines resource-local runtime state, lifecycle and compatible v1 command control.
/// </summary>
public interface IResourceControlPlane
{
    /// <summary>Gets the producer-owned runtime manifest; null denotes a legacy-only resource.</summary>
    ResourceRuntimeManifest? RuntimeManifest => null;

    /// <summary>Gets the immutable transport binding without copying the runtime state catalog.</summary>
    ResourceRuntimeManagement? RuntimeManagement => RuntimeManifest?.Management;

    /// <summary>Gets this invocation's process incarnation for verified direct delivery.</summary>
    Guid ProcessStartId => Guid.Empty;

    /// <summary>Gets whether management bootstrap has admitted this process to serve traffic.</summary>
    bool IsManagementReady => true;

    /// <summary>Holds traffic admission while a managed invocation receives its required state.</summary>
    void RequireStateBootstrap() { }

    /// <summary>Admits this process only after all required state snapshots match.</summary>
    /// <param name="admission">The process and required slot revisions.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>Whether the process was admitted.</returns>
    ValueTask<bool> AdmitStateAsync(ResourceStateAdmission admission, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>Installs the management binding before runtime handlers register their capabilities and configured baselines.</summary>
    /// <param name="management">The logical endpoint and versioned protocol binding.</param>
    /// <exception cref="NotSupportedException">This implementation does not support runtime state.</exception>
    void ConfigureRuntimeManagement(ResourceRuntimeManagement management) => throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);

    /// <summary>Registers a startup capability and its adapter over actual feature state.</summary>
    /// <param name="handler">The hosting adapter over actual feature state.</param>
    /// <exception cref="NotSupportedException">This implementation does not support runtime state.</exception>
    void RegisterStateHandler(IResourceStateHandler handler) => throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);

    /// <summary>Freezes and verifies the discovered runtime catalog before host readiness.</summary>
    void ValidateStateHandlers() { }

    /// <summary>Validates a state change without applying it.</summary>
    /// <param name="name">The state contract.</param>
    /// <param name="key">The state slot.</param>
    /// <param name="desired">The desired value.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The domain validation result.</returns>
    /// <exception cref="NotSupportedException">This implementation does not support runtime state.</exception>
    ValueTask<ResourceStateValidation> ValidateStateAsync(ResourceStateName name, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default) => throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);

    /// <summary>Reads actual state from this process.</summary>
    /// <param name="name">The state contract.</param>
    /// <param name="key">The state slot.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The atomic state observation.</returns>
    /// <exception cref="NotSupportedException">This implementation does not support runtime state.</exception>
    ValueTask<ResourceStateObservation> ReadStateAsync(ResourceStateName name, ResourceStateKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);

    /// <summary>Reconciles one verified desired revision against actual feature state.</summary>
    /// <param name="context">The authenticated target and revision.</param>
    /// <param name="name">The state contract.</param>
    /// <param name="key">The state slot.</param>
    /// <param name="desired">The desired value.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The actual application outcome.</returns>
    /// <exception cref="NotSupportedException">This implementation does not support runtime state.</exception>
    ValueTask<ResourceStateApplyResult> ApplyStateAsync(ResourceStateApplyContext context, ResourceStateName name, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default) => throw new NotSupportedException(ErrorMessages.ResourceStateUnsupported);

    /// <summary>
    /// Gets the area-defined command kinds accepted by this control plane.
    /// </summary>
    IReadOnlyList<string> AcceptedCommandKinds { get; }

    /// <summary>
    /// Gets accepted declarations, including their owners, without transport state.
    /// </summary>
    IReadOnlyList<ResourceCommand> Commands => Array.Empty<ResourceCommand>();

    /// <summary>
    /// Gets the latest observed endpoint snapshot keyed by endpoint name.
    /// </summary>
    IReadOnlyDictionary<string, Uri> ObservedEndpoints { get; }

    /// <summary>Registers the runtime implementation of an advertised command kind.</summary>
    /// <param name="handler">The area-owned mutation handler.</param>
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="ArgumentException">The handler kind is not advertised.</exception>
    /// <exception cref="InvalidOperationException">A handler already exists for the kind.</exception>
    /// <exception cref="NotSupportedException">This control plane does not support registration.</exception>
    void RegisterCommandHandler(IResourceCommandHandler handler) =>
        throw new NotSupportedException("This resource control plane does not support command handlers.");



    /// <summary>Adds a contributor to health, readiness, and liveness aggregation.</summary>
    /// <param name="contributor">The contributor to add.</param>
    void AddHealthContributor(IHealthContributor contributor);

    /// <summary>Records the realized address of a resource endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="address">The realized endpoint address.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="address"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or <paramref name="address"/> is not an endpoint URI.
    /// </exception>
    void ObserveEndpoint(string name, Uri address);

    /// <summary>Checks the aggregate resource health.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate health report.</returns>
    ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource is ready to receive work.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate readiness report.</returns>
    ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource process remains live.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate liveness report.</returns>
    ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default);

    /// <summary>Attaches the built resource host used for graceful stop requests.</summary>
    /// <param name="host">The resource host.</param>
    void AttachHost(IHost host);

    /// <summary>Requests a graceful stop from the attached resource host.</summary>
    /// <param name="cancellationToken">Cancels the request before it is accepted.</param>
    /// <returns>A value task that completes when the request has been accepted.</returns>
    ValueTask RequestStopAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes an area-defined declarative command.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels command execution.</param>
    /// <returns>The area-defined response payload.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind has no registered runtime handler.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses the command.</exception>
    ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a previously applied declaration belonging to the supplied owner.</summary>
    /// <param name="command">The declaration identity to delete.</param>
    /// <param name="cancellationToken">Cancels deletion.</param>
    /// <returns>The area-defined response bytes.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind or deletion is unsupported.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses deletion.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Resource command kind '{command.Kind}' does not support deletion.");
}
