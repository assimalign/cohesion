using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;

public sealed class MapperBuilder
{
    public MapperBuilder()
    {
        this.Options = new MapperOptions();
        this.Profiles = new List<IMapperProfile>();
    }

    internal MapperOptions Options { get; init; }

    internal IList<IMapperProfile> Profiles { get; init; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    public void AddOptions(Action<MapperOptions> configure)
    {
        configure.Invoke(this.Options); 
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="profile"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddProfile<TTarget, TSource>(IMapperProfile<TTarget, TSource> profile)
    {
        if (Profiles.Any(x => x.SourceType == typeof(TSource) && x.TargetType == typeof(TTarget)))
        {
            throw new ArgumentException($"A profile with the same target type: '{profile.TargetType.Name}' and source type: '{profile.SourceType.Name}' has already been added.");
        }

        Profiles.Add(profile);

        IMapperActionDescriptor descriptor = new MapperActionDescriptor<TTarget, TSource>()
        {
            Profiles = this.Profiles,
            MapActions = profile.MapActions
        };

        profile.Configure(descriptor);
    }
}
