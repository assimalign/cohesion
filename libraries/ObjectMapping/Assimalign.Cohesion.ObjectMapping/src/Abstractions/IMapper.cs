using System;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public interface IMapper
{
    /// <summary>
    /// Gets the name of the mapper.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Maps properties from a source object to a destination object using the provided type information.
    /// </summary>
    /// <param name="source">The source object to map from.</param>
    /// <param name="destination">The destination object to map to.</param>
    /// <param name="sourceType">The type of the source object.</param>
    /// <param name="destinationType">The type of the destination object.</param>
    /// <returns>The destination object with mapped properties.</returns>
    object Map(object source, object destination, Type sourceType, Type destinationType);
}