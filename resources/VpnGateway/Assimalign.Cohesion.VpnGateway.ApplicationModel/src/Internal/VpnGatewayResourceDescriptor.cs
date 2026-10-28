using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("VpnGatewayResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class VpnGatewayResourceDescriptor : IVpnGatewayResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal VpnGatewayResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as VpnGatewayResource ?? throw new ArgumentException(
            "The descriptor must wrap a VpnGatewayResource.",
            nameof(inner));
    }

    public VpnGatewayResource Resource { get; }

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

    private sealed class DebugView
    {
        private readonly VpnGatewayResourceDescriptor _target;

        public DebugView(VpnGatewayResourceDescriptor value)
        {
            _target = value;
        }

        public VpnGatewayResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
    }
}
