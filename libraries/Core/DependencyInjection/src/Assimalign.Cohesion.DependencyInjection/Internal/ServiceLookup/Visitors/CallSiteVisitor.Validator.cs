using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class CallSiteValidatorVisitor : CallSiteVisitor<CallSiteValidatorVisitor.CallSiteValidatorState, Type?>
{
    // Keys are services being resolved via GetService, values - first scoped service in their call site tree
    private readonly ConcurrentDictionary<Type, Type> scopedServices = new();

    public void ValidateCallSite(CallSiteService callSite)
    {
        var scoped = VisitCallSite(callSite, default);
        if (scoped != null)
        {
            scopedServices[callSite.ServiceType] = scoped;
        }
    }
    public void ValidateResolution(Type serviceType, IServiceScope scope, IServiceScope rootScope)
    {
        if (ReferenceEquals(scope, rootScope) && scopedServices.TryGetValue(serviceType, out Type? scopedService))
        {
            if (serviceType == scopedService)
            {
                throw new InvalidOperationException(
                    Resources.GetDirectScopedResolvedFromRootExceptionMessage( 
                        serviceType,
                        nameof(ServiceLifetime.Scoped).ToLowerInvariant()));
            }

            throw new InvalidOperationException(
                Resources.GetScopedResolvedFromRootExceptionMessage(
                    serviceType,
                    scopedService,
                    nameof(ServiceLifetime.Scoped).ToLowerInvariant()));
        }
    }

    protected override Type? VisitConstructor(ConstructorCallSite constructorCallSite, CallSiteValidatorState state)
    {
        Type result = null;
        foreach (CallSiteService parameterCallSite in constructorCallSite.ParameterCallSites)
        {
            Type scoped = VisitCallSite(parameterCallSite, state);
            if (result == null)
            {
                result = scoped;
            }
        }
        return result;
    }
    protected override Type VisitEnumerable(EnumerableCallSite enumerableCallSite, CallSiteValidatorState state)
    {
        Type result = null;
        foreach (CallSiteService serviceCallSite in enumerableCallSite.ServiceCallSites)
        {
            Type scoped = VisitCallSite(serviceCallSite, state);
            if (result == null)
            {
                result = scoped;
            }
        }
        return result;
    }
    protected override Type VisitRootCache(CallSiteService singletonCallSite, CallSiteValidatorState state)
    {
        state.Singleton = singletonCallSite;
        return VisitCallSiteMain(singletonCallSite, state);
    }
    protected override Type VisitScopeCache(CallSiteService scopedCallSite, CallSiteValidatorState state)
    {
        // We are fine with having ServiceScopeService requested by singletons
        if (scopedCallSite.ServiceType == typeof(IServiceScopeFactory))
        {
            return null;
        }
        if (state.Singleton != null)
        {
            throw new InvalidOperationException(Resources.GetScopedInSingletonExceptionMessage(
                scopedCallSite.ServiceType,
                state.Singleton.ServiceType,
                nameof(ServiceLifetime.Scoped).ToLowerInvariant(),
                nameof(ServiceLifetime.Singleton).ToLowerInvariant()
                ));
        }

        VisitCallSiteMain(scopedCallSite, state);
        return scopedCallSite.ServiceType;
    }
    protected override Type VisitConstant(ConstantCallSite constantCallSite, CallSiteValidatorState state) => null;
    protected override Type VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, CallSiteValidatorState state) => null;
    protected override Type VisitFactory(FactoryCallSite factoryCallSite, CallSiteValidatorState state) => null;

    internal struct CallSiteValidatorState
    {
        public CallSiteService Singleton { get; set; }
    }
}
