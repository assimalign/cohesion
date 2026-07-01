using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a transition in a resource's observed lifecycle state, raised by
/// <see cref="IApplicationResourceStateManager.StateChanged"/>.
/// </summary>
public sealed class ResourceStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="resource">The resource whose state changed.</param>
    /// <param name="previous">The previous observed state.</param>
    /// <param name="current">The new observed state.</param>
    /// <param name="detail">An optional human-readable detail for the transition.</param>
    public ResourceStateChangedEventArgs(
        ResourceId resource,
        ResourceLifecycle previous,
        ResourceLifecycle current,
        string? detail = null)
    {
        Resource = resource;
        Previous = previous;
        Current = current;
        Detail = detail;
    }

    /// <summary>
    /// The resource whose state changed.
    /// </summary>
    public ResourceId Resource { get; }

    /// <summary>
    /// The previous observed state.
    /// </summary>
    public ResourceLifecycle Previous { get; }

    /// <summary>
    /// The new observed state.
    /// </summary>
    public ResourceLifecycle Current { get; }

    /// <summary>
    /// An optional human-readable detail for the transition.
    /// </summary>
    public string? Detail { get; }
}
