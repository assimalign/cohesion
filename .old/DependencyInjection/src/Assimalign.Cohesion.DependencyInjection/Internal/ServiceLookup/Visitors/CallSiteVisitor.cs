using Assimalign.Cohesion.DependencyInjection.Properties;
using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal abstract class CallSiteVisitor<TArgument, TResult>
{
    private readonly CallSiteStackGuard stackGuard;
    protected CallSiteVisitor() => stackGuard = new CallSiteStackGuard();

    protected virtual TResult VisitCallSite(CallSiteService callSite, TArgument argument)
    {
        if (!stackGuard.TryEnterOnCurrentStack())
        {
            return stackGuard.RunOnEmptyStack((c, a) => VisitCallSite(c, a), callSite, argument);
        }
        return callSite.Cache.Location switch
        {
            CallSiteResultCacheLocation.Root => VisitRootCache(callSite, argument),
            CallSiteResultCacheLocation.Scope => VisitScopeCache(callSite, argument),
            CallSiteResultCacheLocation.Dispose => VisitDisposeCache(callSite, argument),
            CallSiteResultCacheLocation.None => VisitNoCache(callSite, argument),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    protected virtual TResult VisitCallSiteMain(CallSiteService callSite, TArgument argument)
    {
        return callSite.Kind switch
        {
            CallSiteKind.Factory => VisitFactory((FactoryCallSite)callSite, argument),
            CallSiteKind.Enumerable => VisitEnumerable((EnumerableCallSite)callSite, argument),
            CallSiteKind.Constructor => VisitConstructor((ConstructorCallSite)callSite, argument),
            CallSiteKind.Constant => VisitConstant((ConstantCallSite)callSite, argument),
            CallSiteKind.ServiceProvider => VisitServiceProvider((ServiceProviderCallSite)callSite, argument),
            _ => throw new NotSupportedException(Resources.GetCallSiteTypeNotSupportedExceptionMessage(callSite.GetType()))

           
        };
    }
    protected virtual TResult VisitNoCache(CallSiteService callSite, TArgument argument) => VisitCallSiteMain(callSite, argument);
    protected virtual TResult VisitDisposeCache(CallSiteService callSite, TArgument argument) => VisitCallSiteMain(callSite, argument);
    protected virtual TResult VisitRootCache(CallSiteService callSite, TArgument argument) => VisitCallSiteMain(callSite, argument);
    protected virtual TResult VisitScopeCache(CallSiteService callSite, TArgument argument) => VisitCallSiteMain(callSite, argument);
    protected abstract TResult VisitConstructor(ConstructorCallSite constructorCallSite, TArgument argument);
    protected abstract TResult VisitConstant(ConstantCallSite constantCallSite, TArgument argument);
    protected abstract TResult VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, TArgument argument);
    protected abstract TResult VisitEnumerable(EnumerableCallSite enumerableCallSite, TArgument argument);
    protected abstract TResult VisitFactory(FactoryCallSite factoryCallSite, TArgument argument);
}
