using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public sealed class MapperOptions
{
    /// <summary>
    /// 
    /// </summary>
    public string Name { get; set; } = "Default";

    /// <summary>
    /// Specifies whether a target member should be 
    /// ignored when writing nulls or defaults from source member.
    /// <remarks>
    ///     <b>The default is 'Never'</b>
    /// </remarks>
    /// </summary>
    public MapperIgnoreHandling IgnoreHandling { get; set; } = MapperIgnoreHandling.Never;

    /// <summary>
    /// 
    /// </summary>
    public MapperCollectionHandling CollectionHandling { get; set; } = MapperCollectionHandling.Override;

    /// <summary>
    /// 
    /// </summary>
    public List<IMapperProfile> Profiles { get; } = new List<IMapperProfile>();
}
