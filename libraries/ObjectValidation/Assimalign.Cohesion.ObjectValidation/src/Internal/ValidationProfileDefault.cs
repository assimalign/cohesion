using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("ValidationProfileDefault")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(ValidationProfileDefault<>.DebugView))]
internal class ValidationProfileDefault<TValue> : ValidationProfile<TValue>
{
    private readonly Action<IValidationRuleDescriptor<TValue>> _configure;

    public ValidationProfileDefault(Action<IValidationRuleDescriptor<TValue>> configure)
    {
        this._configure = configure;
    }
    public override void Configure(IValidationRuleDescriptor<TValue> descriptor)
    {
        _configure.Invoke(descriptor);
    }

    private sealed class DebugView
    {
        private readonly ValidationProfileDefault<TValue> _target;

        public DebugView(ValidationProfileDefault<TValue> value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.ObjectValidation.IValidationItemQueue ValidationItems => _target.ValidationItems;
        public global::System.Type ValidationType => _target.ValidationType;
    }
}
