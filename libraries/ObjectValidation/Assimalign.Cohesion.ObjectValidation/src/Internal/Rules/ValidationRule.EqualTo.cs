using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("EqualToValidationRule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(EqualToValidationRule<>.DebugView))]
internal sealed class EqualToValidationRule<TValue> : ValidationRuleBase<TValue>
{
    private readonly TValue _argument;

    public EqualToValidationRule(TValue argument)
    {
        this.ArgumentType = typeof(TValue);
        this._argument = argument;
    }

    public Type ArgumentType { get; }

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

            if (!this._argument.Equals(value))
            {
                context.AddFailure(this.Error);
            }

            return true;
        }
        catch (InvalidCastException)
        {
            context = new ValidationContext<TValue>(value);

            if (!this._argument.Equals(value))
            {
                this.Error.Source = $"{this.Error.Source}. Comparison of type '{this.ArgumentType.Name}' and '{this.ValueType.Name}' is not allowed.";
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
        private readonly EqualToValidationRule<TValue> _target;

        public DebugView(EqualToValidationRule<TValue> value)
        {
            _target = value;
        }

        public Type ArgumentType => _target.ArgumentType;
        public string Name => _target.Name;
        public global::System.Type ValueType => _target.ValueType;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationError Error => _target.Error;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationContext ParentContext => _target.ParentContext;
    }
}

