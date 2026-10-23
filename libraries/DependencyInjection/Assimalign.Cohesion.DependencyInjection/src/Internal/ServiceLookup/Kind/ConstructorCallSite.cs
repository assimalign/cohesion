using System;
using System.Reflection;

namespace Assimalign.Cohesion.DependencyInjection.Internal;


[global::System.Diagnostics.DebuggerDisplay("ConstructorCallSite")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ConstructorCallSite : CallSiteService
{
    internal ConstructorInfo ConstructorInfo { get; }
    internal CallSiteService[] ParameterCallSites { get; }

    public ConstructorCallSite(CallSiteResultCache cache, Type serviceType, ConstructorInfo constructorInfo) 
        : this(cache, serviceType, constructorInfo, Array.Empty<CallSiteService>())
    {
    }

    public ConstructorCallSite(CallSiteResultCache cache, Type serviceType, ConstructorInfo constructorInfo, CallSiteService[] parameterCallSites) 
        : base(cache)
    {
        if (!serviceType.IsAssignableFrom(constructorInfo.DeclaringType))
        {
            throw new ArgumentException(ErrorMessageFormatter.GetImplementationTypeCantBeConvertedToServiceType(constructorInfo.DeclaringType, serviceType));
        }

        ServiceType = serviceType;
        ConstructorInfo = constructorInfo;
        ParameterCallSites = parameterCallSites;
    }

    public override Type ServiceType { get; }
    public override Type ImplementationType => ConstructorInfo.DeclaringType ?? throw new NullReferenceException();
    public override CallSiteKind Kind { get; } = CallSiteKind.Constructor;

    private sealed class DebugView
    {
        private readonly ConstructorCallSite _target;

        public DebugView(ConstructorCallSite value)
        {
            _target = value;
        }

        public Type ServiceType => _target.ServiceType;
        public CallSiteKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.DependencyInjection.Internal.CallSiteResultCache Cache => _target.Cache;
        public object Value => _target.Value;
    }
}
