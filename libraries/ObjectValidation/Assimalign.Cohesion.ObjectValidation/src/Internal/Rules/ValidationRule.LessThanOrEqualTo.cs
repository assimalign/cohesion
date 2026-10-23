using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("LessThanOrEqualToValidationRule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(LessThanOrEqualToValidationRule<>.DebugView))]
internal sealed class LessThanOrEqualToValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : struct, IComparable, IComparable<TValue>
{
    private readonly TValue _argument;
    private readonly Func<TValue, TValue, bool> _isLessThanOrEqualTo;

    public LessThanOrEqualToValidationRule(TValue argument)
    {
        this._argument = argument;
        this._isLessThanOrEqualTo = (arg, val) => arg.CompareTo(val) >= 0;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        context = null;

        if (value is null)
        {
            context = new ValidationContext<TValue>(default(TValue));
            context.AddFailure(this.Error);
            return true;
        }
        else if (value is TValue tv)
        {
            return TryValidate(tv, out context);
        }
        else
        {
            return false;
        }
    }

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<TValue>(value);

            if (!_isLessThanOrEqualTo(this._argument, value))
            {
                context.AddFailure(this.Error);
            }

            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }

    private sealed class DebugView
    {
        private readonly LessThanOrEqualToValidationRule<TValue> _target;

        public DebugView(LessThanOrEqualToValidationRule<TValue> value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::System.Type ValueType => _target.ValueType;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationError Error => _target.Error;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationContext ParentContext => _target.ParentContext;
    }
}