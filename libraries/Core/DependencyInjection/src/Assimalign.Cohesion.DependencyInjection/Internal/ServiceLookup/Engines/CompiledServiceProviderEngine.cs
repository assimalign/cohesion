using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal abstract class CompiledServiceProviderEngine : ServiceProviderEngine
{
#if IL_EMIT
    public ILEmitResolverBuilderVisitor ResolverBuilder { get; }
#else
    public CallSiteExpressionResolverBuilderVisitor ResolverBuilder { get; }
#endif
    
    public CompiledServiceProviderEngine(ServiceProvider provider)
    {
        ResolverBuilder = new(provider);
    }

    public override Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite) => ResolverBuilder.Build(callSite);
}
