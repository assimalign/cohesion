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
internal sealed class LegacyDescriptorWrapper(IApplicationResourceDescriptor inner) : IApplicationResourceDescriptor
{
    public IApplicationResource Resource => inner.Resource;
    public ResourcePlan? Plan => inner.Plan;
    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => inner.Dependencies;
    public IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        inner.DependsOn(resource);
        return this;
    }
    public IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        inner.DependsOn(resources);
        return this;
    }
}
