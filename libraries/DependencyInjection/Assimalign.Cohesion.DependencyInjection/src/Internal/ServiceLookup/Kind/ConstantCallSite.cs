using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

using Assimalign.Cohesion.DependencyInjection.Properties;

internal sealed class ConstantCallSite : CallSiteService
{
    private readonly Type _serviceType;
    internal object DefaultValue => Value;

    public ConstantCallSite(Type serviceType, object defaultValue) : this(serviceType, defaultValue, slot: 0)
    {
    }

    /// <param name="slot">
    /// The registration's slot. An instance registration that is not the default must not share the
    /// default registration's key, or the validator would reuse one call site's result for the other.
    /// </param>
    public ConstantCallSite(Type serviceType, object defaultValue, int slot) : base(CallSiteResultCache.None(serviceType, slot))
    {
        this._serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        
        if (defaultValue != null && !serviceType.IsInstanceOfType(defaultValue))
        {
            throw new ArgumentException(Resources.GetConstantCantBeConvertedToServiceType(defaultValue.GetType(), serviceType));
        }

        Value = defaultValue;
    }

    public override Type ServiceType => _serviceType;
    public override Type ImplementationType => DefaultValue?.GetType() ?? _serviceType;
    public override CallSiteKind Kind { get; } = CallSiteKind.Constant;
}
