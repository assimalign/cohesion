using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal class ExpressionsServiceProviderEngine : ServiceProviderEngine
{
    private readonly CallSiteExpressionResolverBuilderVisitor _visitor;

    public ExpressionsServiceProviderEngine(ServiceProvider serviceProvider)
    {
        _visitor = new CallSiteExpressionResolverBuilderVisitor(serviceProvider);
    }

    public override Func<ServiceProviderEngineScope, object> RealizeService(CallSiteService callSite)
    {
        return _visitor.Build(callSite);
    }
}