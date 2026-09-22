using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class IdentityHubResourceDescriptor : IIdentityHubResourceDescriptor
{
    private readonly IResourceCommandDescriptor _inner;

    internal IdentityHubResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner as IResourceCommandDescriptor ?? throw new ArgumentException(
            "The descriptor must support declarative command authoring.", nameof(inner));
        Resource = inner.Resource as IdentityHubResource ?? throw new ArgumentException(
            "The descriptor must wrap a IdentityHubResource.", nameof(inner));
    }

    public IdentityHubResource Resource { get; }
    IApplicationResource IApplicationResourceDescriptor.Resource => Resource;
    public ResourcePlan? Plan => _inner.Plan;
    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _inner.Dependencies;
    public IReadOnlyList<IResourceCommand> Commands => _inner.Commands;

    public IResourceCommand AddCommand<TPayload>(string kind, string key, TPayload payload,
        JsonTypeInfo<TPayload> typeInfo, bool optional = false) =>
        _inner.AddCommand(kind, key, payload, typeInfo, optional);

    public IIdentityHubResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        _inner.DependsOn(resource);
        return this;
    }

    public IIdentityHubResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        _inner.DependsOn(resources);
        return this;
    }

    IApplicationResourceDescriptor IApplicationResourceDescriptor.DependsOn(IApplicationResourceDescriptor resource) => DependsOn(resource);
    IApplicationResourceDescriptor IApplicationResourceDescriptor.DependsOn(params IApplicationResourceDescriptor[] resources) => DependsOn(resources);
}
