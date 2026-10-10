using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal sealed class EnumerableCallSite : CallSiteService
{
    internal Type ItemType { get; }
    internal CallSiteService[] ServiceCallSites { get; }

    public EnumerableCallSite(CallSiteResultCache cache, Type itemType, CallSiteService[] serviceCallSites) : base(cache)
    {
        ItemType = itemType;
        ServiceCallSites = serviceCallSites;
    }

    [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
        Justification = "When ServiceProvider.VerifyAotCompatibility is true, which it is whenever dynamic code is unsupported (NativeAOT), " +
        "CallSiteFactory throws before creating this call site if ItemType is a value type.")]
    public override Type ServiceType => typeof(IEnumerable<>).MakeGenericType(ItemType);

    [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
        Justification = "When ServiceProvider.VerifyAotCompatibility is true, which it is whenever dynamic code is unsupported (NativeAOT), " +
        "CallSiteFactory throws before creating this call site if ItemType is a value type.")]
    public override Type ImplementationType => ItemType.MakeArrayType();
    public override CallSiteKind Kind { get; } = CallSiteKind.Enumerable;
}
