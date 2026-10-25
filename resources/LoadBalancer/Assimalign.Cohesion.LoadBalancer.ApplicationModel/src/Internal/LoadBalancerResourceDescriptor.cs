using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("LoadBalancerResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class LoadBalancerResourceDescriptor : ILoadBalancerResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal LoadBalancerResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as LoadBalancerResource ?? throw new ArgumentException(
            "The descriptor must wrap a LoadBalancerResource.",
            nameof(inner));
    }

    public LoadBalancerResource Resource { get; }

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
        private readonly LoadBalancerResourceDescriptor _target;

        public DebugView(LoadBalancerResourceDescriptor value)
        {
            _target = value;
        }

        public LoadBalancerResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public ResourceGroupName? ResourceGroup => _target.ResourceGroup;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
    }
}
