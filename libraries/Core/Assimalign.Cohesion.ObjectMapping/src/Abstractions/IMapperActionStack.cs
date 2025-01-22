using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public interface IMapperActionStack : 
    IEnumerable<IMapperAction>,
    ICollection,
    IReadOnlyCollection<IMapperAction>
{
    /// <summary>
    /// Returns the most recent mapper action within the 
    /// stack by removing it.
    /// </summary>
    /// <returns><see cref="IMapperAction"/></returns>
    IMapperAction Pop();

    /// <summary>
    /// Attempts to return the most recent mapper action within the 
    /// stack by removing it.
    /// </summary>
    /// <param name="action"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPop(out IMapperAction action);

    /// <summary>
    /// Returns the most recent mapper action within the 
    /// stack without removing it.
    /// </summary>
    /// <returns><see cref="IMapperAction"/></returns>
    IMapperAction Peek();

    /// <summary>
    /// Attempts to return the most recent mapper action within the 
    /// stack without removing it.
    /// </summary>
    /// <param name="action"></param>
    /// <returns><see cref="bool"/></returns>
    bool TryPeek(out IMapperAction action);

    /// <summary>
    /// Adds a new mapper action to the stack.
    /// </summary>
    /// <param name="action"></param>
    void Push(IMapperAction action);
}
