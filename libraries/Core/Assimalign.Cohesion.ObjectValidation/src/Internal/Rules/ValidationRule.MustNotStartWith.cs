using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class MustNotStartWithValidationRule : ValidationRuleBase<string>
{
    private readonly string value;
    private readonly StringComparison comparison;

    public MustNotStartWithValidationRule(string value, StringComparison comparison = StringComparison.InvariantCulture)
    {
        this.value = value;
        this.comparison = comparison;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        if (value is null)
        {
            context = new ValidationContext<string>(default);
            context.AddFailure(this.Error);
            return true;
        }
        else if (value is string str)
        {
            return TryValidate(str, out context);
        }
        else
        {
            context = null;
            return false;
        }
    }

    public override bool TryValidate(string value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<string>(value);

            if (value.StartsWith(this.value, comparison))
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
