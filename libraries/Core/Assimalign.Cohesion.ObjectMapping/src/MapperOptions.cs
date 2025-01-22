
using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>
/// 
/// </summary>
public sealed partial class MapperOptions
{
    /// <summary>
    /// 
    /// </summary>
    public MapperOptions()
    {
        this.Profiles = new List<IMapperProfile>();
    }

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
    internal IList<IMapperProfile> Profiles { get; init; }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="profile"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public MapperOptions AddProfile<TTarget, TSource>(IMapperProfile<TTarget, TSource> profile)
    {
        if (Profiles.Any(x => x.SourceType == typeof(TSource) && x.TargetType == typeof(TTarget)))
        {
            throw new Exception($"A profile with the same target type: '{profile.TargetType.Name}' and source type: '{profile.SourceType.Name}' has already been added.");
        }

        Profiles.Add(profile);

        IMapperActionDescriptor descriptor = new MapperActionDescriptor<TTarget, TSource>()
        {
            Profiles = this.Profiles,
            MapActions = profile.MapActions
        };

        profile.Configure(descriptor);

        return this;
    }
}
