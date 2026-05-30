using System;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Applies the mapping profiles registered for a named mapper, copying values
/// from a source object into a target object.
/// </summary>
/// <remarks>
/// The low-level <see cref="Map(object, object, Type, Type)"/> method is
/// <em>target-first</em>: the target is the instance being populated and the
/// source supplies the values. Callers that want the more familiar
/// "create a new target from a source" ergonomics should use the
/// <see cref="MapperExtensions"/> overloads.
/// </remarks>
public interface IMapper
{
    /// <summary>
    /// Gets the name of the mapper. Mappers are addressable by name through an
    /// <see cref="IMapperFactory"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Copies values from <paramref name="source"/> into <paramref name="target"/>
    /// by applying every registered profile whose source and target types match
    /// <paramref name="sourceType"/> and <paramref name="targetType"/>.
    /// </summary>
    /// <param name="target">The instance to populate.</param>
    /// <param name="source">The instance to read values from.</param>
    /// <param name="targetType">The declared type of <paramref name="target"/>.</param>
    /// <param name="sourceType">The declared type of <paramref name="source"/>.</param>
    /// <returns>The same <paramref name="target"/> instance, with mapped values applied.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="source"/> is not assignable to <paramref name="sourceType"/>
    /// or <paramref name="target"/> is not assignable to <paramref name="targetType"/>.
    /// </exception>
    object Map(object target, object source, Type targetType, Type sourceType);
}
