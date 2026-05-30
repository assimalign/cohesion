using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Ergonomic mapping helpers over <see cref="IMapper"/> and matching helpers over <see cref="IMapperProfile"/>.
/// </summary>
public static partial class MapperExtensions
{
    extension(IMapper mapper)
    {
        /// <summary>
        /// Creates a new <typeparamref name="TTarget"/> and maps <paramref name="source"/> onto it.
        /// </summary>
        /// <typeparam name="TTarget">The target type to create and populate.</typeparam>
        /// <typeparam name="TSource">The source type to map from.</typeparam>
        /// <param name="source">The source instance.</param>
        /// <returns>The newly created and populated target.</returns>
        /// <exception cref="MapperException">Thrown when the mapping result is not a <typeparamref name="TTarget"/>.</exception>
        public TTarget Map<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTarget, TSource>(TSource source) where TTarget : new()
        {
            if (mapper.Map(source!, typeof(TTarget), typeof(TSource)) is TTarget instance)
            {
                return instance;
            }

            throw new MapperException($"The mapping result could not be assigned to type '{typeof(TTarget)}'.");
        }

        /// <summary>
        /// Maps <paramref name="source"/> onto an existing <paramref name="target"/> instance.
        /// </summary>
        /// <typeparam name="TTarget">The target type.</typeparam>
        /// <typeparam name="TSource">The source type to map from.</typeparam>
        /// <param name="target">The target instance to populate.</param>
        /// <param name="source">The source instance.</param>
        /// <returns>The populated target.</returns>
        /// <exception cref="MapperException">Thrown when the mapping result is not a <typeparamref name="TTarget"/>.</exception>
        public TTarget Map<TTarget, TSource>(TTarget target, TSource source)
        {
            if (mapper.Map(target!, source!, typeof(TTarget), typeof(TSource)) is TTarget instance)
            {
                return instance;
            }

            throw new MapperException($"The mapping result could not be assigned to type '{typeof(TTarget)}'.");
        }

        /// <summary>
        /// Creates a new instance of <paramref name="targetType"/> and maps <paramref name="source"/> onto it.
        /// </summary>
        /// <param name="source">The source instance.</param>
        /// <param name="targetType">The target type to create and populate.</param>
        /// <param name="sourceType">The declared source type.</param>
        /// <returns>The newly created and populated target.</returns>
        public object Map(object source, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type targetType, Type sourceType)
        {
            object? target = Activator.CreateInstance(targetType);

            return mapper.Map(target!, source, targetType, sourceType);
        }

        /// <summary>
        /// Creates a new <typeparamref name="TTarget"/> and maps each of the supplied sources onto it
        /// in order, supporting composition of multiple sources into a single target.
        /// </summary>
        /// <typeparam name="TTarget">The target type to create and populate.</typeparam>
        /// <param name="sources">The source instances to compose.</param>
        /// <returns>The populated target.</returns>
        public TTarget Map<TTarget>(params object[] sources) where TTarget : class, new()
        {
            var target = new TTarget();

            foreach (var source in sources)
            {
                target = (TTarget)mapper.Map(target, source, target.GetType(), source.GetType());
            }

            return target;
        }

        /// <summary>
        /// Maps a sequence of sources into a sequence of newly created targets.
        /// </summary>
        /// <typeparam name="TTarget">The target type to create and populate.</typeparam>
        /// <typeparam name="TSource">The source type to map from.</typeparam>
        /// <param name="sources">The source sequence.</param>
        /// <returns>The mapped target sequence.</returns>
        public IEnumerable<TTarget> Map<TTarget, TSource>(IEnumerable<TSource> sources) where TTarget : class, new()
        {
            var targets = new List<TTarget>();

            foreach (var source in sources)
            {
                var target = new TTarget();

                targets.Add(mapper.Map(target, source));
            }

            return targets;
        }
    }


    extension(IMapperProfile profile)
    {
        /// <summary>
        /// Determines whether this profile maps the given target and source types.
        /// </summary>
        /// <param name="targetType">The target type to test.</param>
        /// <param name="sourceType">The source type to test.</param>
        /// <returns><see langword="true"/> when the profile matches both types; otherwise <see langword="false"/>.</returns>
        public bool IsMatch(Type targetType, Type sourceType)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return profile.TargetType == targetType && profile.SourceType == sourceType;
        }
    }
}
