using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// An abstraction of FIFO (first in first out) for fluent validation.
/// </summary>
/// <remarks>
/// Validation rules that are chained together should be handled in 
/// the order they are chained which is why a <see cref="Queue"/> 
/// implementation was used.
/// </remarks>
public interface IValidationRuleQueue :  
    ICollection, 
    IReadOnlyCollection<IValidationRule>
{
    /// <summary>
    /// Returns the most recent validation rule within the 
    /// stack by removing it.
    /// </summary>
    /// <returns><see cref="IValidationRule"/></returns>
    IValidationRule Pop();

    /// <summary>
    /// Attempts to return the most recent validation rule within the 
    /// stack by removing it.
    /// </summary>
    /// <param name="rule"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPop(out IValidationRule rule);

    /// <summary>
    /// Returns the most recent validation rule within the 
    /// stack without removing it.
    /// </summary>
    /// <returns><see cref="IValidationRule"/></returns>
    IValidationRule Peek();

    /// <summary>
    /// Attempts to return the most recent validation rule within the 
    /// stack without removing it.
    /// </summary>
    /// <param name="rule"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPeek(out IValidationRule rule);

    /// <summary>
    /// Adds a new validation rule to the stack.
    /// </summary>
    /// <param name="rule"></param>
    void Push(IValidationRule rule);
}