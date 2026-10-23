using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

[global::System.Diagnostics.DebuggerDisplay("ServiceProviderCallSite")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ServiceProviderCallSite : CallSiteService
{
    public ServiceProviderCallSite() : base(CallSiteResultCache.None(typeof(IServiceProvider)))
    {
    }

    public override Type ServiceType { get; } = typeof(IServiceProvider);
    public override Type ImplementationType { get; } = typeof(ServiceProvider);
    public override CallSiteKind Kind { get; } = CallSiteKind.ServiceProvider;

    private sealed class DebugView
    {
        private readonly ServiceProviderCallSite _target;

        public DebugView(ServiceProviderCallSite value)
        {
            _target = value;
        }

        public Type ServiceType => _target.ServiceType;
        public Type ImplementationType => _target.ImplementationType;
        public CallSiteKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.DependencyInjection.Internal.CallSiteResultCache Cache => _target.Cache;
        public object Value => _target.Value;
    }
}
