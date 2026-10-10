using System;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

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
}
