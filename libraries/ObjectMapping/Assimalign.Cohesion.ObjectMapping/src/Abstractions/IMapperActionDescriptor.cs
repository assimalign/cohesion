using System;
using System.Linq.Expressions;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// The <see cref="IMapperActionDescriptor"/> represents a builder interface for pushing 
/// <see cref="IMapperAction"/>'s into the <see cref="IMapperActionStack"/> which is referenced from the 
/// <see cref="IMapperProfile"/>. 
/// </summary>
public interface IMapperActionDescriptor
{
    /// <summary>
    /// A FIFO (First-In First-Out) collection of Mapper Actions. 
    /// </summary>
    IMapperActionStack MapActions { get; }

    /// <summary>
    /// Adds an <see cref="IMapperAction"/> to the MapAction stack.
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    IMapperActionDescriptor MapAction(IMapperAction action);
}