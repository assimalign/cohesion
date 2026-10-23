using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

[global::System.Diagnostics.DebuggerDisplay("ILEmitServiceProviderEngine")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ILEmitServiceProviderEngine : ServiceProviderEngine
{
    private readonly ILEmitResolverBuilderVisitor _expressionResolverBuilder;
    public ILEmitServiceProviderEngine(ServiceProvider serviceProvider)
    {
        _expressionResolverBuilder = new ILEmitResolverBuilderVisitor(serviceProvider);
    }

    public override Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite)
    {
        return _expressionResolverBuilder.Build(callSite);
    }

    private sealed class DebugView
    {
        public DebugView(ILEmitServiceProviderEngine value)
        {
        }
    }
}
