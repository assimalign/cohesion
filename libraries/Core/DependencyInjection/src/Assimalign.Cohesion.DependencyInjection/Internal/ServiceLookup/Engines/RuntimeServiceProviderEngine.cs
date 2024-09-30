using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class RuntimeServiceProviderEngine : ServiceProviderEngine
{
    private RuntimeServiceProviderEngine() { }

    public override Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite)
    {
        return scope =>
        {
            return CallSiteRuntimeResolverVisitor.Instance.Resolve(callSite, scope);
        };
    }

    public static RuntimeServiceProviderEngine Instance { get; } = new RuntimeServiceProviderEngine();
}
