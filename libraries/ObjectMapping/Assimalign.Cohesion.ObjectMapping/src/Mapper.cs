using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// The default <see cref="IMapper"/> implementation. Holds a fixed set of
/// profiles and applies the matching ones each time <see cref="Map"/> is called.
/// </summary>
public sealed class Mapper : IMapper
{
    private readonly MapperOptions _options;

    /// <summary>
    /// Initializes a new <see cref="Mapper"/> from the supplied options.
    /// </summary>
    /// <param name="options">The options describing the mapper name, profiles, and handling behavior.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public Mapper(MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull<MapperOptions>(options);

        _options = options;
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <summary>
    /// Gets the profiles registered with this mapper.
    /// </summary>
    public IReadOnlyList<IMapperProfile> Profiles => _options.Profiles;

    /// <inheritdoc />
    public object Map(object target, object source, Type targetType, Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(sourceType);

        if (!source.GetType().IsAssignableTo(sourceType))
        {
            throw new ArgumentException($"The source object of type '{source.GetType().FullName}' is not assignable to the declared source type '{sourceType.FullName}'.", nameof(source));
        }

        if (!target.GetType().IsAssignableTo(targetType))
        {
            throw new ArgumentException($"The target object of type '{target.GetType().FullName}' is not assignable to the declared target type '{targetType.FullName}'.", nameof(target));
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
