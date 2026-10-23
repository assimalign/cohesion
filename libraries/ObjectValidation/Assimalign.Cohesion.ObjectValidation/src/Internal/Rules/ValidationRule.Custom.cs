using System;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("CustomValidationRule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(CustomValidationRule<>.DebugView))]
internal sealed class CustomValidationRule<TValue> : ValidationRuleBase<TValue>
{
    private readonly Action<TValue, IValidationContext> _validation;

    public CustomValidationRule(Action<TValue, IValidationContext> validation)
    {
        if (validation is null)
        {
            throw new ArgumentNullException(nameof(validation));
        }

        this._validation = validation;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        if (value is not TValue && value is null)
        {
            return TryValidate(default(TValue), out context);
        }
        else if (value is TValue tv)
        {
            return TryValidate(tv, out context);
        }
        else
        {
            context = null;
            return false;
        }
    }

    // An exception from the delegate propagates: validation fails closed. Caught and reported as "not
    // invoked", it let the value pass with no error (#1292).
    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        context = new ValidationContext<TValue>(value);
        _validation.Invoke(value, context);
        return true;
    }

    private sealed class DebugView
    {
        private readonly CustomValidationRule<TValue> _target;

        public DebugView(CustomValidationRule<TValue> value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::System.Type ValueType => _target.ValueType;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationError Error => _target.Error;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationContext ParentContext => _target.ParentContext;
    }
}
