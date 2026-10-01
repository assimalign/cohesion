using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal struct CallSiteResultCache
{
    /// <summary>
    /// Creates an uncached result that still carries the call site's own key.
    /// </summary>
    /// <remarks>
    /// The scope validator memoizes each call site by its key, so an uncached call site needs a key
    /// that no other call site shares. A shared empty key let a constant registration answer for a
    /// scoped registration of the same service and hide it from validation.
    /// </remarks>
    public static CallSiteResultCache None(Type serviceType, int slot = 0) =>
        new CallSiteResultCache(CallSiteResultCacheLocation.None, new CallSiteServiceCacheKey(serviceType, slot));

    internal CallSiteResultCache(CallSiteResultCacheLocation lifetime, CallSiteServiceCacheKey cacheKey)
    {
        Location = lifetime;
        Key = cacheKey;
    }

    public CallSiteResultCache(ServiceLifetime lifetime, Type type, int slot)
    {
        Debug.Assert(lifetime == ServiceLifetime.Transient || type != null);

        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                Location = CallSiteResultCacheLocation.Root;
                break;
            case ServiceLifetime.Scoped:
                Location = CallSiteResultCacheLocation.Scope;
                break;
            case ServiceLifetime.Transient:
                Location = CallSiteResultCacheLocation.Dispose;
                break;
            default:
                Location = CallSiteResultCacheLocation.None;
                break;
        }
        Key = new CallSiteServiceCacheKey(type, slot);
    }

    public CallSiteResultCacheLocation Location { get; set; }

    public CallSiteServiceCacheKey Key { get; set; }
}
