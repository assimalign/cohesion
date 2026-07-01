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
/// <c>{ Running, Failed }</c>) so that a failed dependency can never deadlock a dependent.
/// Implementations must be race-free: a waiter registers under the same lock that guards
/// the current-state read, so a <see cref="SetState"/> racing a wait cannot be lost.
/// </remarks>
public interface IApplicationResourceStateManager
{
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
    /// Completes when the resource reaches any state in <paramref name="terminals"/>, or when
    /// <paramref name="budget"/> elapses, or when <paramref name="cancellationToken"/> is
    /// signalled; returns the reached state (or the last observed state on timeout).
    /// </summary>
    /// <param name="id">The resource identifier.</param>
    /// <param name="terminals">The set of states any of which completes the wait.</param>
    /// <param name="budget">The maximum time to wait before giving up.</param>
    /// <param name="cancellationToken">Signals that the wait should be abandoned.</param>
    /// <returns>The state that was reached, or the last observed state if the budget elapsed.</returns>
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
