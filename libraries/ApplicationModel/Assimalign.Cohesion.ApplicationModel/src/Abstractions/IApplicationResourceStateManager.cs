using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The level-triggered source of truth for the observed state of resources. A single
/// per-gateway informer is its only writer; controllers and the gateway's reconcile loop
/// read it, and dependency-ordered readiness gating waits on it.
/// </summary>
/// <remarks>
/// The readiness wait completes on any state in a supplied terminal set (for example
/// <c>{ Running, Failed, Stopped }</c>) so that a failed or cleanly stopped dependency can
/// never deadlock a dependent. <see cref="ResourceLifecycle.Degraded"/> is observational;
/// gateways do not include it in their initial-readiness terminal set.
/// Implementations must be race-free: a waiter registers under the same lock that guards
/// the current-state read, so a <see cref="SetState"/> racing a wait cannot be lost.
/// </remarks>
public interface IApplicationResourceStateManager
{
    /// <summary>Returns payload-free command observations for a resource.</summary>
    /// <param name="id">The target resource identifier.</param>
    /// <returns>The latest observation for each owner and command id.</returns>
    IReadOnlyList<ResourceCommandObservation> GetCommandObservations(ResourceId id);

    /// <summary>Records the provider's outcome for a declared command.</summary>
    /// <param name="id">The target resource identifier.</param>
    /// <param name="observation">The immutable provider observation.</param>
    void SetCommandObservation(ResourceId id, ResourceCommandObservation observation);

    /// <summary>Removes an observation after the provider confirms teardown.</summary>
    /// <param name="id">The target resource identifier.</param>
    /// <param name="owner">The declaring application.</param>
    /// <param name="commandId">The command identifier.</param>
    void RemoveCommandObservation(ResourceId id, string owner, string commandId);

    /// <summary>
    /// Returns the current observed state of a resource, or <see cref="ResourceLifecycle.Unknown"/>
    /// if nothing has been observed yet.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <returns>The current observed lifecycle state.</returns>
    ResourceLifecycle GetState(ResourceId id);

    /// <summary>
    /// Records an observed state for a resource — an idempotent level write, safe to call
    /// repeatedly with the same value. Optionally records the resource's allocated endpoints.
    /// Raises <see cref="StateChanged"/> when the state actually changes.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="state">The newly observed state.</param>
    /// <param name="detail">An optional human-readable detail (for example a failure reason).</param>
    /// <param name="observedEndpoints">The resource's allocated endpoints, when known.</param>
    void SetState(
        ResourceId id,
        ResourceLifecycle state,
        string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null);

    /// <summary>
    /// Returns the observed (allocated) endpoints for a resource once known — for example
    /// an OS-assigned port or a stable cluster service address — or an empty list otherwise.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <returns>The resource's observed endpoints.</returns>
    IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id);

    /// <summary>
    /// Completes when the resource reaches any state in <paramref name="terminals"/> or when
    /// <paramref name="budget"/> elapses. Cancellation abandons the wait by throwing an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="terminals">The set of states any of which completes the wait.</param>
    /// <param name="budget">The maximum time to wait before giving up.</param>
    /// <param name="cancellationToken">Signals that the wait should be abandoned.</param>
    /// <returns>The state that was reached, or the last observed state if the budget elapsed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="terminals"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="budget"/> is outside the supported timer range and is not
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a resource's observed state changes.
    /// </summary>
    event EventHandler<ResourceStateChangedEventArgs> StateChanged;
}
