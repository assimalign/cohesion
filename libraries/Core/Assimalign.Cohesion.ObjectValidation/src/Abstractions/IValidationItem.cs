using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationItem
{
    /// <summary>
    /// A stack of rules to be evaluated against the given context: <see cref="Evaluate(IValidationContext)"/>.
    /// </summary>
    IValidationRuleQueue ItemRuleStack { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    void Evaluate(IValidationContext context);
}


/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TValue"></typeparam>
public interface IValidationItem<T, TValue> : IValidationItem
{
    /// <summary>
    /// The item expression to be validated.
    /// </summary>
    Expression<Func<T, TValue>> ItemExpression { get; }
}