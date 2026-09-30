using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class CallSiteValidatorVisitor : CallSiteVisitor<CallSiteValidatorVisitor.CallSiteValidatorState, Type?>
{
    // Keyed by call-site cache key (service type and slot), never by service type alone: a service
    // type's registrations that are not the default have call sites of their own, and a scoped one
    // must not flag the default registration. The value is the first scoped service in the call
    // site's tree, or null when the tree has none.
    private readonly ConcurrentDictionary<CallSiteServiceCacheKey, Type?> _scopedServices = new();

    public void ValidateCallSite(CallSiteService callSite) => VisitCallSite(callSite, default);

    public void ValidateResolution(CallSiteService callSite, IServiceScope scope, IServiceScope rootScope)
    {
        if (ReferenceEquals(scope, rootScope)
            && _scopedServices.TryGetValue(callSite.Cache.Key, out Type? scopedService)
            && scopedService != null)
        {
            Type serviceType = callSite.ServiceType;
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

    protected override Type? VisitCallSite(CallSiteService callSite, CallSiteValidatorState state)
    {
        // Walk each call site's tree once. Without the memo a graph that shares dependencies is
        // walked once per path through it, which grows exponentially with its depth.
        if (!_scopedServices.TryGetValue(callSite.Cache.Key, out Type? firstScopedService))
        {
            firstScopedService = base.VisitCallSite(callSite, state);
            _scopedServices[callSite.Cache.Key] = firstScopedService;
        }

        // Checked on every visit, memoized ones included: the memo records what a tree contains, not
        // which singleton reached it.
        if (firstScopedService != null && state.Singleton != null)
        {
            throw new InvalidOperationException(Resources.GetScopedInSingletonExceptionMessage(
                firstScopedService,
                state.Singleton.ServiceType,
                nameof(ServiceLifetime.Scoped).ToLowerInvariant(),
                nameof(ServiceLifetime.Singleton).ToLowerInvariant()));
        }

        return firstScopedService;
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

        // A singleton consuming this service is reported by VisitCallSite, which sees memoized
        // trees too.
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
