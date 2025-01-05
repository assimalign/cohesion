using System;
using System.Threading;
using System.Reflection;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class CallSiteRuntimeResolverVisitor : CallSiteVisitor<CallSiteRuntimeResolverVisitor.CallSiteRuntimeResolverContext, object>
{
    public static CallSiteRuntimeResolverVisitor Instance { get; } = new();

    private CallSiteRuntimeResolverVisitor()
    {
    }


    public object Resolve(CallSiteService callSite, ServiceProviderEngineScope scope)
    {
        // Fast path to avoid virtual calls if we already have the cached value in the root scope
        if (scope.IsRootScope && callSite.Value is object cached)
        {
            return cached;
        }
        return VisitCallSite(callSite, new CallSiteRuntimeResolverContext
        {
            Scope = scope
        });
    }
    private object VisitCache(CallSiteService callSite, CallSiteRuntimeResolverContext context, ServiceProviderEngineScope serviceProviderEngine, CallSiteRuntimeResolverLock lockType)
    {
        bool lockTaken = false;
        object sync = serviceProviderEngine.Sync;
        Dictionary<CallSiteServiceCacheKey, object> resolvedServices = serviceProviderEngine.ResolvedServices;
        // Taking locks only once allows us to fork resolution process
        // on another thread without causing the deadlock because we
        // always know that we are going to wait the other thread to finish before
        // releasing the lock
        if ((context.AcquiredLocks & lockType) == 0)
        {
            Monitor.Enter(sync, ref lockTaken);
        }

        try
        {
            // Note: This method has already taken lock by the caller for resolution and access synchronization.
            // For scoped: takes a dictionary as both a resolution lock and a dictionary access lock.
            if (resolvedServices.TryGetValue(callSite.Cache.Key, out object resolved))
            {
                return resolved;
            }

            resolved = VisitCallSiteMain(callSite, new CallSiteRuntimeResolverContext
            {
                Scope = serviceProviderEngine,
                AcquiredLocks = context.AcquiredLocks | lockType
            });
            serviceProviderEngine.CaptureDisposable(resolved);
            resolvedServices.Add(callSite.Cache.Key, resolved);
            return resolved;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(sync);
            }
        }
    }

    #region Visitor Base Overrides
    protected override object VisitDisposeCache(CallSiteService transientCallSite, CallSiteRuntimeResolverContext context)
    {
        return context.Scope.CaptureDisposable(VisitCallSiteMain(transientCallSite, context));
    }
    protected override object VisitConstructor(ConstructorCallSite constructorCallSite, CallSiteRuntimeResolverContext context)
    {
        object[] parameterValues;
        if (constructorCallSite.ParameterCallSites.Length == 0)
        {
            parameterValues = Array.Empty<object>();
        }
        else
        {
            parameterValues = new object[constructorCallSite.ParameterCallSites.Length];
            for (int index = 0; index < parameterValues.Length; index++)
            {
                parameterValues[index] = VisitCallSite(constructorCallSite.ParameterCallSites[index], context);
            }
        }

        return constructorCallSite.ConstructorInfo.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: parameterValues, culture: null);
    }
    protected override object VisitRootCache(CallSiteService callSite, CallSiteRuntimeResolverContext context)
    {
        if (callSite.Value is object value)
        {
            // Value already calculated, return it directly
            return value;
        }

        var lockType = CallSiteRuntimeResolverLock.Root;
        ServiceProviderEngineScope serviceProviderEngine = context.Scope.RootProvider.Root;

        lock (callSite)
        {
            // Lock the callsite and check if another thread already cached the value
            if (callSite.Value is object resolved)
            {
                return resolved;
            }

            resolved = VisitCallSiteMain(callSite, new CallSiteRuntimeResolverContext
            {
                Scope = serviceProviderEngine,
                AcquiredLocks = context.AcquiredLocks | lockType
            });
            serviceProviderEngine.CaptureDisposable(resolved);
            callSite.Value = resolved;
            return resolved;
        }
    }
    protected override object VisitScopeCache(CallSiteService callSite, CallSiteRuntimeResolverContext context)
    {
        // Check if we are in the situation where scoped service was promoted to singleton
        // and we need to lock the root
        return context.Scope.IsRootScope ?
            VisitRootCache(callSite, context) :
            VisitCache(callSite, context, context.Scope, CallSiteRuntimeResolverLock.Scope);
    }
    protected override object VisitConstant(ConstantCallSite constantCallSite, CallSiteRuntimeResolverContext context)
    {
        return constantCallSite.DefaultValue;
    }
    protected override object VisitServiceProvider(ServiceProviderCallSite serviceProviderCallSite, CallSiteRuntimeResolverContext context)
    {
        return context.Scope;
    }
    protected override object VisitEnumerable(EnumerableCallSite enumerableCallSite, CallSiteRuntimeResolverContext context)
    {
        var array = Array.CreateInstance(
            enumerableCallSite.ItemType,
            enumerableCallSite.ServiceCallSites.Length);

        for (int index = 0; index < enumerableCallSite.ServiceCallSites.Length; index++)
        {
            object value = VisitCallSite(enumerableCallSite.ServiceCallSites[index], context);
            array.SetValue(value, index);
        }
        return array;
    }
    protected override object VisitFactory(FactoryCallSite factoryCallSite, CallSiteRuntimeResolverContext context)
    {
        return factoryCallSite.Factory(context.Scope);
    }
    #endregion

    [Flags]
    internal enum CallSiteRuntimeResolverLock
    {
        Scope = 1,
        Root = 2
    }
    internal struct CallSiteRuntimeResolverContext
    {
        public ServiceProviderEngineScope Scope { get; set; }
        public CallSiteRuntimeResolverLock AcquiredLocks { get; set; }
    }
}
