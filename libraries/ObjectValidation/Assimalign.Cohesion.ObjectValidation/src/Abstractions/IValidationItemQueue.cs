using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// A first-in, first-out queue of the validation items a profile declares, one per member.
/// </summary>
/// <remarks>
/// A profile's items are evaluated in the order they are declared, so the queue is enumerated, indexed,
/// peeked and popped from the item pushed first to the item pushed last.
/// </remarks>
public interface IValidationItemQueue : IEnumerable<IValidationItem>
{
    /// <summary>
    /// Gets the number of items in the queue.
    /// </summary>
    int Count { get; }
    /// <summary>
    /// Gets the item at <paramref name="index"/>, counted from the front of the queue: index 0 is the item
    /// pushed first.
    /// </summary>
    /// <param name="index">The zero-based position from the front of the queue.</param>
    /// <returns>The item at that position.</returns>
    IValidationItem this[int index] { get; }
    /// <summary>
    /// Removes and returns the item at the front of the queue: the item pushed first, which is evaluated
    /// first.
    /// </summary>
    /// <returns>The item at the front of the queue.</returns>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    IValidationItem Pop();

    /// <summary>
    /// Attempts to remove and return the item at the front of the queue: the item pushed first.
    /// </summary>
    /// <param name="item">The item at the front of the queue, when the queue is not empty.</param>
    /// <returns><see langword="true"/> when an item was removed; <see langword="false"/> when the queue is empty.</returns>
    bool TryPop(out IValidationItem item);

    /// <summary>
    /// Returns the item at the front of the queue, the item pushed first, without removing it.
    /// </summary>
    /// <returns>The item at the front of the queue.</returns>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    IValidationItem Peek();

    /// <summary>
    /// Attempts to return the item at the front of the queue, the item pushed first, without removing it.
    /// </summary>
    /// <param name="item">The item at the front of the queue, when the queue is not empty.</param>
    /// <returns><see langword="true"/> when the queue holds an item; <see langword="false"/> when it is empty.</returns>
    bool TryPeek(out IValidationItem item);

    /// <summary>
    /// Adds an item to the back of the queue, so it is evaluated after every item already queued.
    /// </summary>
    /// <param name="item">The item to add.</param>
    void Push(IValidationItem item);
}
