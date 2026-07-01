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
        IApplicationResource resource,
        IApplicationModel model,
        IApplicationResourceStateManager state,
        IReadOnlyList<IApplicationResource> dependencies,
        IResourceArtifact artifact)
    {
        Resource = resource;
        Model = model;
        State = state;
        Dependencies = dependencies;
        _artifact = artifact;
    }

    public IApplicationResource Resource { get; }

    public IApplicationModel Model { get; }

    public IApplicationResourceStateManager State { get; }

    public IReadOnlyList<IApplicationResource> Dependencies { get; }

    public T GetArtifact<T>()
        where T : class, IResourceArtifact
        => _artifact as T
           ?? throw new InvalidOperationException(
               $"The gathered artifact for resource '{Resource.Name}' is '{_artifact.GetType().Name}', not '{typeof(T).Name}'.");
}
