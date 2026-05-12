using System;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class CustomValidationRule<TValue> : ValidationRuleBase<TValue>
{
    private readonly Action<TValue, IValidationContext> validation;

    public CustomValidationRule(Action<TValue, IValidationContext> validation)
    {
        if (validation is null)
        {
            throw new ArgumentNullException(nameof(validation));
        }

        this.validation = validation;
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
            validation.Invoke(value, context);
            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }
}
