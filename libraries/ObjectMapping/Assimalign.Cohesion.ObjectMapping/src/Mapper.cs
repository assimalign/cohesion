using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

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
        _options = ArgumentNullException.ThrowIfNull<MapperOptions>(options);
    }

    /// <inheritdoc />
    public IReadOnlyList<IMapperProfile> Profiles => _options.Profiles.AsReadOnly();

    /// <inheritdoc />
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

        if (!targetType.IsAssignableTo(targetType.GetType()))
        {

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

            if (profile.SourceType == sourceType && profile.TargetType == targetType)
            {
                foreach (IMapperAction action in profile.MapActions)
                {
                    action.Invoke(context);
                }
            }
        }

        return target;
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IMapper Create(Action<MapperBuilder> configure)
    {
        var builder = new MapperBuilder();

        configure.Invoke(builder);

        return new Mapper(builder.Options);
    }
}
