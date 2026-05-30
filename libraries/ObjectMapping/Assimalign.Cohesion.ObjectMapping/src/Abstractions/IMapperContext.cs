using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Carries the state for a single mapping invocation: the source and target
/// instances, the available profiles, and the handling behavior.
/// </summary>
public interface IMapperContext
{
    /// <summary>
    /// The instance of the source currently being mapped.
    /// </summary>
    object Source { get; }

    /// <summary>
    /// The instance of the target currently being mapped.
    /// </summary>
    object Target { get; }

    /// <summary>
    /// The collection of profiles encapsulated by the mapper. Used to resolve
    /// nested profiles for complex and enumerable members.
    /// </summary>
    IReadOnlyList<IMapperProfile> Profiles { get; }

    /// <summary>
    /// Specifies whether a target member should be ignored when writing nulls or
    /// defaults from the source member.
    /// </summary>
    /// <remarks>
    ///     <b>The default is 'Never'.</b>
    /// </remarks>
    MapperIgnoreHandling IgnoreHandling { get; }

    /// <summary>
    /// Specifies whether an enumerable member should be overridden or merged together.
    /// </summary>
    MapperCollectionHandling CollectionHandling { get; }
}
