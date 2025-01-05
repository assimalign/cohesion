using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

/// <summary>
/// Summary description for ServiceCallSite
/// </summary>
internal abstract class CallSiteService
{
    protected CallSiteService(CallSiteResultCache cache)
    {
        Cache = cache;
    }

    public abstract Type ServiceType { get; }
    public abstract Type? ImplementationType { get; }
    public abstract CallSiteKind Kind { get; }
    public CallSiteResultCache Cache { get; }
    public object Value { get; set; }

    public bool CaptureDisposable =>
        ImplementationType == null ||
        typeof(IDisposable).IsAssignableFrom(ImplementationType) ||
        typeof(IAsyncDisposable).IsAssignableFrom(ImplementationType);
}
