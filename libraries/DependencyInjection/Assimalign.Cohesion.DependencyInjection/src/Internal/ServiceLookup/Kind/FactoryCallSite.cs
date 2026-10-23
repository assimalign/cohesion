using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

[global::System.Diagnostics.DebuggerDisplay("FactoryCallSite")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class FactoryCallSite : CallSiteService
{

    public FactoryCallSite(CallSiteResultCache cache, Type serviceType, Func<IServiceProvider, object> factory) : base(cache)
    {
        Factory = factory;
        ServiceType = serviceType;
    }

    public Func<IServiceProvider, object> Factory { get; }
    public override Type ServiceType { get; }
    public override Type ImplementationType => null;
    public override CallSiteKind Kind { get; } = CallSiteKind.Factory;

    private sealed class DebugView
    {
        private readonly FactoryCallSite _target;

        public DebugView(FactoryCallSite value)
        {
            _target = value;
        }

        public Func<IServiceProvider, object> Factory => _target.Factory;
        public Type ServiceType => _target.ServiceType;
        public Type ImplementationType => _target.ImplementationType;
        public CallSiteKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.DependencyInjection.Internal.CallSiteResultCache Cache => _target.Cache;
        public object Value => _target.Value;
    }
}
