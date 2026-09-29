using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Everything an <see cref="IApplicationResourceController"/> needs to reconcile one
/// resource: its immutable plan, the model it belongs to, already-admitted and observed
/// dependencies, resolved inputs, the shared observed-state store, and the gathered artifact.
/// </summary>
public interface IResourceControlContext
{
    /// <summary>
    /// The immutable built descriptor being reconciled.
    /// </summary>
    IApplicationResourceDescriptor Descriptor { get; }

    /// <summary>
    /// The resource being reconciled.
    /// </summary>
    IApplicationResource Resource { get; }

    /// <summary>
    /// The platform-neutral realization plan compiled by the selected controller.
    /// </summary>
    ResourcePlan Plan { get; }

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
    /// The mount contents and bootstrap credential resolved for this reconcile pass.
    /// </summary>
    ResourceInputs Inputs { get; }

    /// <summary>
    /// Immutable observations for referenced dependencies at the start of this reconcile pass.
    /// </summary>
    IReadOnlyList<ResourceDependencyObservation> ObservedDependencies { get; }

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
