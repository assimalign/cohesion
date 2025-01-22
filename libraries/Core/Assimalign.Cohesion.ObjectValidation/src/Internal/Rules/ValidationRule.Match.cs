using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class MatchValidationRule : ValidationRuleBase<string>
{
    private readonly string pattern;
    private readonly RegexOptions? options;

    public MatchValidationRule(string pattern, RegexOptions? options = null)
    {
        this.pattern = pattern;
        this.options = options;
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

            if (this.options is not null && !Regex.IsMatch(value, this.pattern, this.options ?? default))
            {
                context.AddFailure(this.Error);
            }
            else if (!Regex.IsMatch(value, this.pattern))
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

