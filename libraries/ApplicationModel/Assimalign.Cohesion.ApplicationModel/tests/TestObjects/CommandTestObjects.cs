using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CommandTestJsonContext : JsonSerializerContext;

internal sealed record TestResourceCommand(string Id, string Kind, string Key,
    IApplicationResource Target, ApplicationName Owner, ReadOnlyMemory<byte> Payload, bool Optional) : IResourceCommand;

// Deliberately matches older wrappers: no command or adapter interface, just delegated Resource identity.
internal sealed class LegacyDescriptorWrapper : IApplicationResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyDescriptorWrapper"/> class.
    /// </summary>
    /// <param name="inner">The descriptor whose resource identity and dependencies the wrapper delegates to.</param>
    public LegacyDescriptorWrapper(IApplicationResourceDescriptor inner)
    {
        _inner = inner;
    }

    public IApplicationResource Resource => _inner.Resource;
    public ResourcePlan? Plan => _inner.Plan;
    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _inner.Dependencies;
    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        _inner.DependsOn(resource);
        return this;
    }
    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        _inner.DependsOn(resources);
        return this;
    }
}
