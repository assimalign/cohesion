using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TValue"></typeparam>
public interface IValidationRuleBuilder<TValue>
{
    /// <summary>
    /// The validation rule being configured within the rule builder.
    /// </summary>
    IValidationItem ValidationItem { get; }

    /// <summary>
    /// Adds a custom validation rule that encapsulates custom logic.
    /// </summary>
    /// <param name="validation"></param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> Custom(Action<TValue, IValidationContext> validation);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must be null.
    /// </summary>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> Null();

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must be null with 
    /// a configurable custom error.
    /// </summary>
    /// <param name="configure">A delegate to configure a custom validation error.</param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> Null(Action<IValidationError> configure);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must not be null.
    /// </summary>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> NotNull();

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must not be null with 
    /// a configurable custom error.
    /// </summary>
    /// <param name="configure">A delegate to configure a custom validation error.</param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> NotNull(Action<IValidationError> configure);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must be equal to <paramref name="value"/>.
    /// </summary>
    /// <param name="value"></param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> EqualTo(TValue value);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must be equal to <paramref name="value"/>>.
    /// </summary>
    /// <param name="value"></param>
    /// <param name="configure">A delegate to configure a custom validation error.</param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> EqualTo(TValue value, Action<IValidationError> configure);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must not be equal to <paramref name="value"/>.
    /// </summary>
    /// <param name="value"></param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> NotEqualTo(TValue value);

    /// <summary>
    /// Creates a rule specifying that <typeparamref name="TValue"/> must not be equal to <paramref name="value"/>.
    /// </summary>
    /// <param name="value"></param>
    /// <param name="configure">A delegate to configure a custom validation error.</param>
    /// <returns><see cref="IValidationRuleBuilder{TValue}"/></returns>
    IValidationRuleBuilder<TValue> NotEqualTo(TValue value, Action<IValidationError> configure);
}