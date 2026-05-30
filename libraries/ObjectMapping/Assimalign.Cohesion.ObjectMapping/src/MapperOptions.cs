using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Configuration for a <see cref="Mapper"/>: its name, handling behavior, and profiles.
/// </summary>
public sealed class MapperOptions
{
    /// <summary>
    /// The name the mapper is addressable by through an <see cref="IMapperFactory"/>. Defaults to <c>"Default"</c>.
    /// </summary>
    public string Name { get; set; } = "Default";

    /// <summary>
    /// Specifies whether a target member should be ignored when writing nulls or
    /// defaults from the source member.
    /// </summary>
    /// <remarks>
    ///     <b>The default is 'Never'.</b>
    /// </remarks>
    public MapperIgnoreHandling IgnoreHandling { get; set; } = MapperIgnoreHandling.Never;

    /// <summary>
    /// Specifies whether an enumerable member should be overridden or merged together.
    /// Defaults to <see cref="MapperCollectionHandling.Override"/>.
    /// </summary>
    public MapperCollectionHandling CollectionHandling { get; set; } = MapperCollectionHandling.Override;

    /// <summary>
    /// The profiles registered with the mapper.
    /// </summary>
    public List<IMapperProfile> Profiles { get; } = new List<IMapperProfile>();
}
