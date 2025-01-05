using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal struct CallSiteResultCache
{
    public static CallSiteResultCache None { get; } = new CallSiteResultCache(CallSiteResultCacheLocation.None, CallSiteServiceCacheKey.Empty);

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
