using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// A first-in, first-out queue of the validation rules chained to one validation item.
/// </summary>
/// <remarks>
/// Rules that are chained together are evaluated in the order they are chained, so the queue is enumerated,
/// copied, peeked and popped from the rule pushed first to the rule pushed last.
/// </remarks>
public interface IValidationRuleQueue :  
    ICollection, 
    IReadOnlyCollection<IValidationRule>
{
    /// <summary>
    /// Removes and returns the rule at the front of the queue: the rule pushed first, which is evaluated
    /// first.
    /// </summary>
    /// <returns>The rule at the front of the queue.</returns>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    IValidationRule Pop();

    /// <summary>
    /// Attempts to remove and return the rule at the front of the queue: the rule pushed first.
    /// </summary>
    /// <param name="rule">The rule at the front of the queue, when the queue is not empty.</param>
    /// <returns><see langword="true"/> when a rule was removed; <see langword="false"/> when the queue is empty.</returns>
    bool TryPop(out IValidationRule rule);

    /// <summary>
    /// Returns the rule at the front of the queue, the rule pushed first, without removing it.
    /// </summary>
    /// <returns>The rule at the front of the queue.</returns>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    IValidationRule Peek();

    /// <summary>
    /// Attempts to return the rule at the front of the queue, the rule pushed first, without removing it.
    /// </summary>
    /// <param name="rule">The rule at the front of the queue, when the queue is not empty.</param>
    /// <returns><see langword="true"/> when the queue holds a rule; <see langword="false"/> when it is empty.</returns>
    bool TryPeek(out IValidationRule rule);

    /// <summary>
    /// Adds a rule to the back of the queue, so it is evaluated after every rule already queued.
    /// </summary>
    /// <param name="rule">The rule to add.</param>
    void Push(IValidationRule rule);
}