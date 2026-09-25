using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal sealed class MessageHubResourceDescriptor : IMessageHubResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal MessageHubResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as MessageHubResource ?? throw new ArgumentException(
            "The descriptor must wrap a MessageHubResource.",
            nameof(inner));
    }

    public MessageHubResource Resource { get; }

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
