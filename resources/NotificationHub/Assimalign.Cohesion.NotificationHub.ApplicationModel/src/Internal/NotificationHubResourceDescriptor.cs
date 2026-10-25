using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("NotificationHubResourceDescriptor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class NotificationHubResourceDescriptor : INotificationHubResourceDescriptor
{
    private readonly IApplicationResourceDescriptor _inner;

    internal NotificationHubResourceDescriptor(IApplicationResourceDescriptor inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Resource = inner.Resource as NotificationHubResource ?? throw new ArgumentException(
            "The descriptor must wrap a NotificationHubResource.",
            nameof(inner));
    }

    public NotificationHubResource Resource { get; }

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
        private readonly NotificationHubResourceDescriptor _target;

        public DebugView(NotificationHubResourceDescriptor value)
        {
            _target = value;
        }

        public NotificationHubResource Resource => _target.Resource;
        public ResourcePlan? Plan => _target.Plan;
        public ResourceGroupName? ResourceGroup => _target.ResourceGroup;
        public IReadOnlyList<IApplicationResourceDescriptor> Dependencies => _target.Dependencies;
    }
}
