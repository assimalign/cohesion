using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal.Rules;


internal class NotEmptyValidationRule<TValue> : ValidationRuleBase<TValue> 
    where TValue : IEnumerable
{
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
        context = new ValidationContext<TValue>(value);

        if (IsEmpty(value))
        {
            context.AddFailure(this.Error);
        }

        return true;
    }

    private bool IsEmpty(object member)
    {
        return member switch
        {
            null => true,
            string      stringValue when string.IsNullOrWhiteSpace(stringValue) => true, // May not need this since string is IEnumerable
            ICollection collection  when collection.Count == 0 => true,
            Array       array       when array.Length == 0 => true,
            IEnumerable enumerable  when !enumerable.Cast<object>().Any() => true,
            _ => false
        };
    }
}