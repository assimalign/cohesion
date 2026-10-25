using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("WebResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class WebResourceDescriptor : IWebResourceDescriptor
{
    private readonly IResourceCommandDescriptor _inner;

    internal WebResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner as IResourceCommandDescriptor ?? throw new ArgumentException(
            ErrorMessages.CommandDescriptorRequired, nameof(inner));
    }

    public IApplicationResource Resource => _inner.Resource;
    public ResourcePlan? Plan => _inner.Plan;
    public ResourceGroupName? ResourceGroup => _inner.ResourceGroup;
    public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _inner.Dependencies;
    public IReadOnlyList<IResourceCommand> Commands => _inner.Commands;

    public IResourceCommand AddCommand<TPayload>(string kind, string key, TPayload payload,
        JsonTypeInfo<TPayload> typeInfo, bool optional = false) =>
        _inner.AddCommand(kind, key, payload, typeInfo, optional);

    public IWebResourceDescriptor DependsOn(IApplicationResourceDescriptor resource)
    {
        _inner.DependsOn(resource);
        return this;
    }

    public IWebResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources)
    {
        _inner.DependsOn(resources);
        return this;
    }

    IApplicationResourceDescriptor IApplicationResourceDescriptor.DependsOn(IApplicationResourceDescriptor resource) => DependsOn(resource);
    IApplicationResourceDescriptor IApplicationResourceDescriptor.DependsOn(params IApplicationResourceDescriptor[] resources) => DependsOn(resources);

    private sealed class DebugView
    {
        private readonly WebResourceDescriptor _target;

        public DebugView(WebResourceDescriptor value)
        {
            _target = value;
        }

        public IApplicationResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public ResourceGroupName? ResourceGroup => _target.ResourceGroup;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
        public IReadOnlyList<IResourceCommand> Commands => _target.Commands;
    }
}
