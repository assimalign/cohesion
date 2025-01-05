using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationCondition
{
    /// <summary>
    /// Represents a 
    /// <remarks>
    ///     <b>This Should be set a </b>
    /// </remarks>
    /// </summary>
    IValidationItemQueue ValidationItems { get; }
}


/// <summary>
/// Represents a validation item for which other validation rules should be applied. 
/// </summary>
/// <remarks>
/// The logical flow is best described as if (Condition is true/false) then { Run Validation Rules }
/// </remarks>
/// <typeparam name="T"></typeparam>
public interface IValidationCondition<T> : IValidationCondition
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="condition">The Condition in which the child validation rule collection should be applied.</param>
    /// <param name="configure">The validation to </param>
    /// <returns></returns>
    IValidationCondition<T> When(Expression<Func<T, bool>> condition, Action<IValidationRuleDescriptor<T>> configure);
}