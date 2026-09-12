using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel;

internal sealed class RezolvrResourceDescriptor : IRezolvrResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal RezolvrResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as RezolvrResource ?? throw new ArgumentException(
            "The descriptor must wrap a RezolvrResource.",
            nameof(inner));
    }

    public RezolvrResource Resource { get; }

    IApplicationResource IApplicationResourceDescriptor.Resource => Resource;

    public ResourcePlan? Plan => _inner.Plan;

    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _inner.Dependencies;

    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        _inner.DependsOn(resource);
        return this;
    }

    public IApplicationResourceDescriptor DependsOn(
        params IApplicationResourceDescriptor[] resources)
    {
        _inner.DependsOn(resources);
        return this;
    }
}
