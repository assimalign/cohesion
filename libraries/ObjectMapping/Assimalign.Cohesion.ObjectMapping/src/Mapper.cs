using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.ObjectMapping.Internal;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// The default <see cref="IMapper"/> implementation. Holds a fixed set of
/// profiles and applies the matching ones each time <see cref="Map"/> is called.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("Mapper {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    internal TTarget MapTyped<TTarget, TSource>(TTarget target, TSource source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        List<IMapperProfile> profiles = _options.Profiles;
        var execution = new MapperExecution(target, source, profiles, _options.IgnoreHandling, _options.CollectionHandling);
        for (int i = 0; i < profiles.Count; i++)
        {
            IMapperProfile profile = profiles[i];
            if (profile.IsMatch(typeof(TTarget), typeof(TSource)))
            {
                if (profile is IMapperProfileExecutor<TTarget, TSource> typed)
                {
                    typed.Invoke(target, source, ref execution);
                }
                else
                {
                    execution.Invoke(profile);
                }
            }
        }

        return target;
    }

    /// <inheritdoc />
    public object Map(object target, object source, Type targetType, Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(sourceType);

        if (!source.GetType().IsAssignableTo(sourceType))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.MappingSourceTypeMismatch, source.GetType().FullName, sourceType.FullName), nameof(source));
        }

        if (!target.GetType().IsAssignableTo(targetType))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.MappingTargetTypeMismatch, target.GetType().FullName, targetType.FullName), nameof(target));
        }

        List<IMapperProfile> profiles = _options.Profiles;
        var execution = new MapperExecution(target, source, profiles, _options.IgnoreHandling, _options.CollectionHandling);

        for (int i = 0; i < profiles.Count; i++)
        {
            IMapperProfile profile = profiles[i];

            if (profile.IsMatch(targetType, sourceType))
            {
                execution.Invoke(profile);
            }
        }

        return target;
    }

    private sealed class DebugView
    {
        private readonly Mapper _target;

        public DebugView(Mapper value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public IReadOnlyList<IMapperProfile> Profiles => _target.Profiles;
    }
}
