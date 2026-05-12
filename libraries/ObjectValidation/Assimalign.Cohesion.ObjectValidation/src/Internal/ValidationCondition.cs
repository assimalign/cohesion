using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;


namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationCondition<T> : IValidationCondition<T>
{
    public ValidationCondition()
    {
        this.ValidationItems = new ValidationItemQueue();
    }

    public IValidationItemQueue ValidationItems { get; set; }
    public Expression<Func<T, bool>> Condition { get; set; }

    public IValidationCondition<T> When(Expression<Func<T, bool>> condition, Action<IValidationRuleDescriptor<T>> configure)
    {
        var validationCondition = new ValidationCondition<T>()
        {
            Condition = condition,
            ValidationItems = this.ValidationItems
        };
        var descriptor = new ValidationRuleDescriptor<T>()
        {
            ValidationItems = new ValidationItemQueue(),
            ValidationCondition = condition.Compile(),
        };

        configure.Invoke(descriptor);

        foreach (var item in descriptor.ValidationItems)
        {
            this.ValidationItems.Push(item);
        }

        return validationCondition;
    }
}

