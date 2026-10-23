using System;

namespace Assimalign.Cohesion.DependencyInjection.Internal;


[global::System.Diagnostics.DebuggerDisplay("ConstantCallSite")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
            throw new ArgumentException(ErrorMessageFormatter.GetConstantCantBeConvertedToServiceType(defaultValue.GetType(), serviceType));
        }

        Value = defaultValue;
    }

    public override Type ServiceType => _serviceType;
    public override Type ImplementationType => DefaultValue?.GetType() ?? _serviceType;
    public override CallSiteKind Kind { get; } = CallSiteKind.Constant;

    private sealed class DebugView
    {
        private readonly ConstantCallSite _target;

        public DebugView(ConstantCallSite value)
        {
            _target = value;
        }

        public Type ServiceType => _target.ServiceType;
        public CallSiteKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.DependencyInjection.Internal.CallSiteResultCache Cache => _target.Cache;
        public object Value => _target.Value;
    }
}
