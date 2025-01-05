using System;
using System.Collections.Generic;

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

    public override Type ServiceType => typeof(IEnumerable<>).MakeGenericType(ItemType);
    public override Type ImplementationType => ItemType.MakeArrayType();
    public override CallSiteKind Kind { get; } = CallSiteKind.Enumerable;
}
