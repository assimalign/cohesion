using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

[global::System.Diagnostics.DebuggerDisplay("EnumerableCallSite")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly EnumerableCallSite _target;

        public DebugView(EnumerableCallSite value)
        {
            _target = value;
        }

        public CallSiteKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.DependencyInjection.Internal.CallSiteResultCache Cache => _target.Cache;
        public object Value => _target.Value;
    }
}
