using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class LessThanValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : struct, IComparable, IComparable<TValue>
{
    private readonly TValue argument;
    private readonly Func<TValue, TValue, bool> isLessThan;

    public LessThanValidationRule(TValue argument)
    { 
        this.argument = argument;
        this.isLessThan = (arg, val) =>
        {
            var result = arg.CompareTo(val);

            if (result > 0)
            {
                return true;
            }
            return false;
        };
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

            if (!isLessThan(this.argument, value))
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
}
