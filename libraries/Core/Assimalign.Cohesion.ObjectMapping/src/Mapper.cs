using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

/// <summary>
/// 
/// </summary>
public sealed class Mapper : IMapper
{
    private readonly MapperOptions options;
    private readonly IList<IMapperProfile> profiles;

    private static ConcurrentBag<int> references = new();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public Mapper(IEnumerable<IMapperProfile> profiles, MapperOptions options)
    {
        this.profiles = profiles.ToList();
        this.options = options;
    }

    /// <inheritdoc cref="IMapper.Profiles"/>
    public IEnumerable<IMapperProfile> Profiles => this.profiles;

    public TTarget Map<TTarget, TSource>(TSource source)
        where TTarget : new()
    {
        if (source is null)
        {
            throw new ArgumentNullException("source");
        }

        var results = this.Map(source, typeof(TTarget), typeof(TSource));

        if (results is TTarget instance)
        {
            return instance;
        }
        else
        {
            throw new Exception("");
        }
    }
    public TTarget Map<TTarget, TSource>(TTarget target, TSource source)
    {
        if (target is null)
        {
            throw new ArgumentNullException("target");
        }
        if (source is null)
        {
            throw new ArgumentNullException("source");
        }

        if (this.Map(target, source, typeof(TTarget), typeof(TSource)) is TTarget instance)
        {
            return instance;
        }
        else
        {
            throw new Exception("");
        }
    }
    public object Map(object source, Type targetType, Type sourceType)
    {
        if (source is null)
        {
            throw new ArgumentNullException("source");
        }
        try
        {
            object target = Activator.CreateInstance(targetType);

            return this.Map(target, source, targetType, sourceType);
        }
        catch (Exception exception)
        {
            throw new MapperInstanceCreationException(targetType, exception);
        }
    }
    public object Map(object target, object source, Type targetType, Type sourceType)
    {
        if (target is null)
        {
            throw new ArgumentNullException("target");
        }
        if (source is null)
        {
            throw new ArgumentNullException("source");
        }

        var context = new MapperContext(target, source)
        {
            Profiles = this.Profiles,
            IgnoreHandling = options.IgnoreHandling,
            CollectionHandling = options.CollectionHandling
        };

        foreach (var profile in profiles)
        {
            if (profile.SourceType == sourceType && profile.TargetType == targetType)
            {
                foreach (var action in profile.MapActions)
                {
                    action.Invoke(context);
                }

                break;
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

        return new Mapper(builder.Profiles, builder.Options);
    }
}
