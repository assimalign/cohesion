using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationRuleDescriptor
{
    /// <summary>
    /// 
    /// </summary>
    IValidationItemQueue ValidationItems { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    IValidationRuleDescriptor RuleFor(IValidationItem item);
}

/// <summary>
/// Initializes a validation rule builder.
/// </summary>
public interface IValidationRuleDescriptor<T> : IValidationRuleDescriptor
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TValue">A member expression is either a field or property of a Type.</typeparam>
    /// <param name="expression"></param>
    IValidationRuleBuilder<TValue> RuleFor<TValue>(Expression<Func<T, TValue>> expression);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="expression"></param>
    /// <returns></returns>
    IValidationRuleBuilder<TValue> RuleForEach<TValue>(Expression<Func<T, IEnumerable<TValue>>> expression);

    /// <summary>
    /// Encapsulates a set of validation rules that are evaluated if the condition <paramref name="condition"/> is true.
    /// </summary>
    /// <param name="condition">What condition is required</param>
    /// <param name="configure">The validation to </param>
    /// <returns></returns>
    IValidationCondition<T> When(Expression<Func<T, bool>> condition, Action<IValidationRuleDescriptor<T>> configure);
}