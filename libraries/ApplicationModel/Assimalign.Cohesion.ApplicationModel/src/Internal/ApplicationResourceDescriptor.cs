using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationResourceDescriptor"/>: a resource plus its
/// dependency edges. Reference identity is used for dependency comparisons, so the
/// descriptor returned by <see cref="IApplicationBuilder.AddResource(IApplicationResource)"/>
/// is the same instance referenced by <c>DependsOn</c>.
/// </summary>
internal sealed class ApplicationResourceDescriptor : IApplicationResourceDescriptor
{
    private readonly List<IApplicationResourceDescriptor> _dependencies = new();

    public ApplicationResourceDescriptor(IApplicationResource resource)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    public IApplicationResource Resource { get; }

    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _dependencies;

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (ReferenceEquals(resource, this))
        {
            throw new InvalidOperationException($"Resource '{Resource.Name}' cannot depend on itself.");
        }

        if (!_dependencies.Contains(resource))
        {
            _dependencies.Add(resource);
        }

        return this;
    }

    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var resource in resources)
        {
            DependsOn(resource);
        }

        return this;
    }
}
