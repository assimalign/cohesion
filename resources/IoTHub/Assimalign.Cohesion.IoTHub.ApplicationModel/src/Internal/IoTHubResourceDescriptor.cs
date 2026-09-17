using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.IoTHub.ApplicationModel;

internal sealed class IoTHubResourceDescriptor : IIoTHubResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal IoTHubResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as IoTHubResource ?? throw new ArgumentException(
            "The descriptor must wrap a IoTHubResource.",
            nameof(inner));
    }

    public IoTHubResource Resource { get; }

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
