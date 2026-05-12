using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;


internal sealed class MustContainValidationRule<TValue, TContains> : ValidationRuleBase<TValue>
    where TValue : IEnumerable, IEnumerable<TContains>
    where TContains : notnull, IEquatable<TContains>
{
    private readonly TContains contains;

    public MustContainValidationRule(TContains containes)
    {
        this.contains = containes;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
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
            context = null;
            return false;
        }
    }

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<TValue>(value);

            if (!value.Contains(this.contains))
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

