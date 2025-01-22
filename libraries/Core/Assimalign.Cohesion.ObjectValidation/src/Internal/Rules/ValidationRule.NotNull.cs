namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class NotNullValidationRule<T, TValue> : ValidationRuleBase<TValue>
{
    public NotNullValidationRule() { }

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
        else if (value is not null && value is TValue tv)
        {
            context = new ValidationContext<TValue>(tv);
            return true;
        }
        else
        {
            return false;
        }
    }

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        return TryValidate(value as object, out context);
    }
}