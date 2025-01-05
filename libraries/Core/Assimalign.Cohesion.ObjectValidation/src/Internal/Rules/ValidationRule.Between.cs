using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class BetweenValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : struct, IComparable, IComparable<TValue>
{
    private readonly TValue lowerBound;
    private readonly TValue upperBound;
    private readonly Func<TValue, TValue, TValue, bool> isOutOfBounds;

    public BetweenValidationRule(TValue lowerBound, TValue upperBound) 
    {
        this.lowerBound = lowerBound;
        this.upperBound = upperBound;
        this.isOutOfBounds = (lower, upper, value) =>
        {
            var lowerResults = lower.CompareTo(value);
            var upperResults = upper.CompareTo(value);

            return lowerResults >= 0 || upperResults <= 0;
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
        if (value is TValue tv)
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

            if (isOutOfBounds(this.lowerBound, this.upperBound, value))
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
