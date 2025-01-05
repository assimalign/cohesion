using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class ConstantCallSite : CallSiteService
{
    private readonly Type serviceType;
    internal object DefaultValue => Value;

    public ConstantCallSite(Type serviceType, object defaultValue) : base(CallSiteResultCache.None)
    {
        this.serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        
        if (defaultValue != null && !serviceType.IsInstanceOfType(defaultValue))
        {
            throw new ArgumentException(Resources.GetConstantCantBeConvertedToServiceType(defaultValue.GetType(), serviceType));
        }

        Value = defaultValue;
    }

    public override Type ServiceType => serviceType;
    public override Type ImplementationType => DefaultValue?.GetType() ?? serviceType;
    public override CallSiteKind Kind { get; } = CallSiteKind.Constant;
}
