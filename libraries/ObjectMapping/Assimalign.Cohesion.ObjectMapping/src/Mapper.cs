using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public sealed class Mapper : IMapper
{
    private readonly MapperOptions _options;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public Mapper(MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull<MapperOptions>(options);

        _options = options;
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <inheritdoc />
    public IReadOnlyList<IMapperProfile> Profiles => _options.Profiles;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <param name="targetType"></param>
    /// <param name="sourceType"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public object Map(object target, object source, Type targetType, Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(sourceType);

        if (!sourceType.IsAssignableTo(source.GetType()))
        {
            throw new ArgumentException($"The source type '{sourceType.FullName}' is not assignable from the actual source type '{source.GetType().FullName}'.");
        }

        if (!targetType.IsAssignableTo(target.GetType()))
        {
            throw new ArgumentException($"The target type '{targetType.FullName}' is not assignable from the actual target type '{target.GetType().FullName}'.");
        }
 
        MapperContext context = new MapperContext(target, source)
        {
            Profiles = Profiles,
            IgnoreHandling = _options.IgnoreHandling,
            CollectionHandling = _options.CollectionHandling
        };

        for (int i = 0; i < Profiles.Count; i++)
        {
            IMapperProfile profile = Profiles[i];

            if (profile.IsMatch(targetType, sourceType))
            {
                foreach (IMapperAction action in profile.MapActions)
                {
                    action.Invoke(context);
                }
            }
        }

        return target;
    }
}
