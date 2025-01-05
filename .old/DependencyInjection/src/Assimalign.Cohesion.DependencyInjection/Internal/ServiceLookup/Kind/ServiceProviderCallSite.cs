using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class ServiceProviderCallSite : CallSiteService
{
    public ServiceProviderCallSite() : base(CallSiteResultCache.None)
    {
    }

    public override Type ServiceType { get; } = typeof(IServiceProvider);
    public override Type ImplementationType { get; } = typeof(ServiceProvider);
    public override CallSiteKind Kind { get; } = CallSiteKind.ServiceProvider;
}
