using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

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
}
