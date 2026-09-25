using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class MatchValidationRule : ValidationRuleBase<string>
{
    private readonly string _pattern;
    private readonly RegexOptions? _options;

    public MatchValidationRule(string pattern, RegexOptions? options = null)
    {
        this._pattern = pattern;
        this._options = options;
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

            if (this._options is not null && !Regex.IsMatch(value, this._pattern, this._options ?? default))
            {
                context.AddFailure(this.Error);
            }
            else if (!Regex.IsMatch(value, this._pattern))
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

