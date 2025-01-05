using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public interface IValidationItemQueue : IEnumerable<IValidationItem>
{
    /// <summary>
    /// 
    /// </summary>
    int Count { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    IValidationItem this[int index] { get; }
    /// <summary>
    /// Returns the most recent validation item within the 
    /// stack by removing it.
    /// </summary>
    /// <returns><see cref="IValidationItem"/></returns>
    IValidationItem Pop();

    /// <summary>
    /// Attempts to return the most recent validation item within the 
    /// stack by removing it.
    /// </summary>
    /// <param name="item"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPop(out IValidationItem item);

    /// <summary>
    /// Returns the most recent validation item within the 
    /// stack without removing it.
    /// </summary>
    /// <returns><see cref="IValidationItem"/></returns>
    IValidationItem Peek();

    /// <summary>
    /// Attempts to return the most recent validation item within the 
    /// stack without removing it.
    /// </summary>
    /// <param name="item"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPeek(out IValidationItem item);

    /// <summary>
    /// Adds a new validation item to the stack.
    /// </summary>
    /// <param name="item"></param>
    void Push(IValidationItem item);
}
