using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The default <see cref="IResourceControlContext"/> passed to a controller for one resource.
/// </summary>
internal sealed class ResourceControlContext : IResourceControlContext
{
    private readonly IResourceArtifact _artifact;

    public ResourceControlContext(
        IApplicationResourceDescriptor descriptor,
        IApplicationModel model,
        IApplicationResourceStateManager state,
        IReadOnlyList<IApplicationResource> dependencies,
        IResourceArtifact artifact,
        IReadOnlyList<ResourceDependencyObservation> observedDependencies)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        ObservedDependencies = observedDependencies
            ?? throw new ArgumentNullException(nameof(observedDependencies));
        Inputs = ResourceInputs.Empty;
    }

    public IApplicationResourceDescriptor Descriptor { get; }

    public IApplicationResource Resource => Descriptor.Resource;

    public ResourcePlan Plan => Descriptor.Plan
        ?? throw new InvalidOperationException(
            $"Resource '{Resource.Name}' has no realization plan. Build the application model before reconciliation.");

    public IApplicationModel Model { get; }

    public IApplicationResourceStateManager State { get; }

    public IReadOnlyList<IApplicationResource> Dependencies { get; }

    public ResourceInputs Inputs { get; private set; }

    internal ResourceTelemetryInjection? Telemetry { get; set; }

    public IReadOnlyList<ResourceDependencyObservation> ObservedDependencies { get; }

    public void SetInputs(ResourceInputs inputs)
    {
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
    }

    public T GetArtifact<T>()
        where T : class, IResourceArtifact
        => _artifact as T
           ?? throw new InvalidOperationException(
               $"The gathered artifact for resource '{Resource.Name}' is '{_artifact.GetType().Name}', not '{typeof(T).Name}'.");
}
