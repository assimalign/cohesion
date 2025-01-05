using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal static class ServiceLookupHelpers
{

    private const BindingFlags LookupFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly MethodInfo? ArrayEmptyMethodInfo = typeof(Array).GetMethod(nameof(Array.Empty));

    internal static readonly MethodInfo? InvokeFactoryMethodInfo = typeof(Func<IServiceProvider, object>)
        .GetMethod(nameof(Func<IServiceProvider, object>.Invoke), LookupFlags);

    internal static readonly MethodInfo? CaptureDisposableMethodInfo = typeof(ServiceProviderEngineScope)
        .GetMethod(nameof(ServiceProviderEngineScope.CaptureDisposable), LookupFlags);

    internal static readonly MethodInfo? TryGetValueMethodInfo = typeof(IDictionary<CallSiteServiceCacheKey, object>)
        .GetMethod(nameof(IDictionary<CallSiteServiceCacheKey, object>.TryGetValue), LookupFlags);

    internal static readonly MethodInfo? ResolveCallSiteAndScopeMethodInfo = typeof(CallSiteRuntimeResolverVisitor)
        .GetMethod(nameof(CallSiteRuntimeResolverVisitor.Resolve), LookupFlags);

    internal static readonly MethodInfo? AddMethodInfo = typeof(IDictionary<CallSiteServiceCacheKey, object>)
        .GetMethod(nameof(IDictionary<CallSiteServiceCacheKey, object>.Add), LookupFlags);

    internal static readonly MethodInfo? MonitorEnterMethodInfo = typeof(Monitor)
        .GetMethod(nameof(Monitor.Enter), BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(object), typeof(bool).MakeByRefType() }, null);
    
    internal static readonly MethodInfo? MonitorExitMethodInfo = typeof(Monitor)
        .GetMethod(nameof(Monitor.Exit), BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(object) }, null);

    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2060:MakeGenericMethod",
        Justification = "Calling Array.Empty<T>() is safe since the T doesn't have trimming annotations.")]
    internal static MethodInfo GetArrayEmptyMethodInfo(Type itemType) =>
        ArrayEmptyMethodInfo.MakeGenericMethod(itemType);
}
