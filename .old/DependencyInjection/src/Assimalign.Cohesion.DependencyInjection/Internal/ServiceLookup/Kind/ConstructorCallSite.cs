using System;
using System.Reflection;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

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
            throw new ArgumentException(Resources.GetImplementationTypeCantBeConvertedToServiceType(constructorInfo.DeclaringType, serviceType));
        }

        ServiceType = serviceType;
        ConstructorInfo = constructorInfo;
        ParameterCallSites = parameterCallSites;
    }

    public override Type ServiceType { get; }
    public override Type ImplementationType => ConstructorInfo.DeclaringType ?? throw new NullReferenceException();
    public override CallSiteKind Kind { get; } = CallSiteKind.Constructor;
}
