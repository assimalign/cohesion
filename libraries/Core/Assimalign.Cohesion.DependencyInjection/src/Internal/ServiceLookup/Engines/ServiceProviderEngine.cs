using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal abstract class ServiceProviderEngine
{
    public abstract Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite);
}
