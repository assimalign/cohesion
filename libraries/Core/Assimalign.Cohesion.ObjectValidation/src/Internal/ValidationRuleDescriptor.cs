using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationRuleDescriptor : IValidationRuleDescriptor
{
    public IValidationItemQueue ValidationItems { get; set; }

    public IValidationRuleDescriptor RuleFor(IValidationItem item)
    {
        this.ValidationItems.Push(item);
        return this;
    }
}

internal sealed class ValidationRuleDescriptor<T> : IValidationRuleDescriptor<T>
{
    public IValidationItemQueue ValidationItems { get; set; }

    public Func<T, bool> ValidationCondition { get; set; } 

    IValidationRuleDescriptor IValidationRuleDescriptor.RuleFor(IValidationItem item)
    {
        this.ValidationItems.Push(item);
        return this;
    }

    public IValidationRuleBuilder<TValue> RuleFor<TValue>(Expression<Func<T, TValue>> expression)
    {
        if (expression is null)
        {
            throw new ArgumentNullException(
                paramName: nameof(expression),
                message: $"A null expression passed occurred at RuleFor<TValue>(Expression<Func<T, TValue>> expression).");
        }
        if (expression.Body is not MemberExpression)
        {
            throw new ValidationInvalidMemberException(expression);
        }

        var item = new ValidationItem<T, TValue>()
        {
            ItemExpression = expression,
            ValidationCondition = this.ValidationCondition
        };

        this.ValidationItems.Push(item);

        return new ValidationRuleBuilder<T, TValue>(item);
    }
    public IValidationRuleBuilder<TValue> RuleForEach<TValue>(Expression<Func<T, IEnumerable<TValue>>> expression)
    {
        if (expression is null)
        {
            throw new ArgumentNullException(
                paramName: nameof(expression),
                message: $"A null expression passed occurred at RuleForEach<TValue>(Expression<Func<T, IEnumerable<TValue>>> expression).");
        }
        if (expression.Body is not MemberExpression)
        {
            throw new ValidationInvalidMemberException(expression);
        }

        var item = new ValidationItemCollection<T, TValue>()
        {
            ItemExpression = expression,
            ValidationCondition = this.ValidationCondition
        };

        this.ValidationItems.Push(item);

        return new ValidationRuleBuilder<T, TValue>(item);
    }
    public IValidationCondition<T> When(Expression<Func<T, bool>> condition, Action<IValidationRuleDescriptor<T>> configure)
    {
        var validationCondition = new ValidationCondition<T>()
        {
            Condition = condition,
            ValidationItems = this.ValidationItems,
        };
        var descriptor = new ValidationRuleDescriptor<T>()
        {
            ValidationItems = new ValidationItemQueue(),
            ValidationCondition = condition.Compile()
        };
        
        configure.Invoke(descriptor);

        foreach(var item in descriptor.ValidationItems)
        {
            this.ValidationItems.Push(item);
        }

        return validationCondition;
    }
}