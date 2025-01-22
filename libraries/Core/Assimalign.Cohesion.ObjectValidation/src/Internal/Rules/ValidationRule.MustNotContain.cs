using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;

internal sealed class MustNotContainValidationRule<TValue, TContains> : ValidationRuleBase<TValue>
    where TValue : IEnumerable, IEnumerable<TContains>
    where TContains : notnull, IEquatable<TContains>
{
    private readonly TContains contains;

    public MustNotContainValidationRule(TContains contains)
    {
        this.contains = contains;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        if (value is null)
        {
            // If there a re no values in the enumerable then by default validation has passed
            context = new ValidationContext<TValue>(default(TValue));
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

            if (value.Contains(this.contains))
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

