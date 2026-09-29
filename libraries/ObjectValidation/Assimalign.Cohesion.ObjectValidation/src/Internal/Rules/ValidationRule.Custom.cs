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

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<TValue>(value);
            _validation.Invoke(value, context);
            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }
}
