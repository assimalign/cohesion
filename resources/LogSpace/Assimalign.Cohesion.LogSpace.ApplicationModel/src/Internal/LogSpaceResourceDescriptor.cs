using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("LogSpaceResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class LogSpaceResourceDescriptor : ILogSpaceResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal LogSpaceResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as LogSpaceResource ?? throw new ArgumentException(
            "The descriptor must wrap a LogSpaceResource.",
            nameof(inner));
    }

    public LogSpaceResource Resource { get; }

    IApplicationResource IApplicationResourceDescriptor.Resource => Resource;

    public ResourcePlan? Plan => _inner.Plan;
    public ResourceGroupName? ResourceGroup => _inner.ResourceGroup;

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

    private sealed class DebugView
    {
        private readonly LogSpaceResourceDescriptor _target;

        public DebugView(LogSpaceResourceDescriptor value)
        {
            _target = value;
        }

        public LogSpaceResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public ResourceGroupName? ResourceGroup => _target.ResourceGroup;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
    }
}
