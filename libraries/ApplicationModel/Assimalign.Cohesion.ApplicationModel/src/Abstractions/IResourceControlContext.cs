using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Everything an <see cref="IApplicationResourceController"/> needs to reconcile one
/// resource: the resource itself, the model it belongs to, its already-realized
/// dependencies, the shared observed-state store, and the gathered deployable artifact.
/// </summary>
public interface IResourceControlContext
{
    /// <summary>
    /// The resource being reconciled.
    /// </summary>
    IApplicationResource Resource { get; }

    /// <summary>
    /// The model the resource belongs to.
    /// </summary>
    IApplicationModel Model { get; }

    /// <summary>
    /// The shared, level-triggered observed-state store.
    /// </summary>
    IApplicationResourceStateManager State { get; }

    /// <summary>
    /// The resources this resource depends on, already realized when reconciliation runs.
    /// </summary>
    IReadOnlyList<IApplicationResource> Dependencies { get; }

    /// <summary>
    /// The deployable artifact the gateway gathered for this resource, as the requested
    /// concrete shape (for example <see cref="IExecutableArtifact"/> or
    /// <see cref="IContainerImageArtifact"/>).
    /// </summary>
    /// <typeparam name="T">The expected artifact type.</typeparam>
    /// <returns>The gathered artifact as <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">The artifact is not assignable to <typeparamref name="T"/>.</exception>
    T GetArtifact<T>() where T : class, IResourceArtifact;
}
