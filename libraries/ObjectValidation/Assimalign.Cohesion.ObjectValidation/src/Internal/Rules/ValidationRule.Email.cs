using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class EmailValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : IEnumerable<char>
{
    public EmailValidationRule() { }

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
            // Only a string is checked. Any other TValue, and a null passed to this overload, reports "not invoked",
            // as it did when Regex.IsMatch threw on the null that 'as string' gave it; #1293 decides that behavior.
            if (value is not string address)
            {
                context = null;
                return false;
            }

            context = new ValidationContext<TValue>(value);

            // Linear in the input's length, and capped at the RFC 5321 address size (#1377).
            if (!EmailAddressFormat.IsValid(address))
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