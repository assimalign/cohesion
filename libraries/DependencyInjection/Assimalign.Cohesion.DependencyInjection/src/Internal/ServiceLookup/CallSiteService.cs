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

    /// <summary>
    /// Set while the runtime resolver creates this call site's root-cached value.
    /// </summary>
    /// <remarks>
    /// Read and written only while holding the lock on this call site, so only the thread creating
    /// the value can observe it set.
    /// </remarks>
    internal bool IsResolving { get; set; }

    public bool CaptureDisposable =>
        ImplementationType == null ||
        typeof(IDisposable).IsAssignableFrom(ImplementationType) ||
        typeof(IAsyncDisposable).IsAssignableFrom(ImplementationType);
}
