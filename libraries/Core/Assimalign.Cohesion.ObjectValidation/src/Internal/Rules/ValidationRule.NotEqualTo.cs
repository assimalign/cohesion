namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class NotEqualToValidationRule<TValue> : ValidationRuleBase<TValue>
{
    private readonly TValue argument;

    public NotEqualToValidationRule(TValue argument)
    {
        this.argument = argument;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        context = null;

        if (argument is null && value is not null)
        {
            context = new ValidationContext<TValue>(default(TValue));
            context.AddFailure(this.Error);
            return true;
        }
        else if (value is null)
        {
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

            if (this.argument.Equals(value))
            {
                context.AddFailure(this.Error);
            }

            return true;
        }
        //catch (InvalidCastException)
        //{
        //    context = new ValidationContext<TValue>(value);

        //    if (this.argument.Equals(value))
        //    {
        //        this.Error.Source = $"{this.Error.Source}. Comparison of type '{this.ArgumentType.Name}' and '{this.ValueType.Name}' is not allowed.";
        //        context.AddFailure(this.Error);
        //    }

        //    return true;
        //}
        catch
        {
            context = null;
            return false;
        }
    }
}

