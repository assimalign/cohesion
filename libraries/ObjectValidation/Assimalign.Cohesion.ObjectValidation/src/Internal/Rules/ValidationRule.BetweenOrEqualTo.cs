using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class BetweenOrEqualToValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : struct, IComparable, IComparable<TValue>
{
    private readonly TValue _lowerBound;
    private readonly TValue _upperBound;
    private readonly Func<TValue, TValue, TValue, bool> _isOutOfBounds;

    public BetweenOrEqualToValidationRule(TValue lowerBound, TValue upperBound)
    {
        this._lowerBound = lowerBound;
        this._upperBound = upperBound;
        this._isOutOfBounds = (lower, upper, value) =>
        {
            var lowerResults = lower.CompareTo(value);
            var upperResults = upper.CompareTo(value);

            return lowerResults > 0 || upperResults < 0;
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

            if (_isOutOfBounds(this._lowerBound, this._upperBound, value))
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