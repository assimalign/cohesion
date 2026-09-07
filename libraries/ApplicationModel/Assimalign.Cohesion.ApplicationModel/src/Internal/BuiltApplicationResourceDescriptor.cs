using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class BuiltApplicationResourceDescriptor : IApplicationResourceDescriptor
{
    private IReadOnlyList<IApplicationResourceDescriptor>? _dependencies;

    public BuiltApplicationResourceDescriptor(IApplicationResource resource)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    public IApplicationResource Resource { get; }

    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies =>
        _dependencies ?? Array.Empty<IApplicationResourceDescriptor>();

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource) =>
        throw new InvalidOperationException(
            "A built application model is immutable; declare dependency edges on the builder descriptor before Build().");

    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources) =>
        throw new InvalidOperationException(
            "A built application model is immutable; declare dependency edges on the builder descriptor before Build().");

    public void SetDependencies(IReadOnlyList<IApplicationResourceDescriptor> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        if (_dependencies is not null)
        {
            throw new InvalidOperationException("Built descriptor dependencies have already been initialized.");
        }

        var copy = new IApplicationResourceDescriptor[dependencies.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = dependencies[index];
        }

        _dependencies = new ReadOnlyCollection<IApplicationResourceDescriptor>(copy);
    }
}
