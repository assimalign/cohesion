using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping;

public static partial class MapperExtensions
{
    extension(IMapper mapper)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TTarget"></typeparam>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public TTarget Map<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTarget, TSource>(TSource source) where TTarget : new()
        {
            if (mapper.Map(source!, typeof(TTarget), typeof(TSource)) is TTarget instance)
            {
                return instance;
            }

            throw new Exception("");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TTarget"></typeparam>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="target"></param>
        /// <param name="source"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public TTarget Map<TTarget, TSource>(TTarget target, TSource source)
        {
            if (mapper.Map(target!, source!, typeof(TTarget), typeof(TSource)) is TTarget instance)
            {
                return instance;
            }
            else
            {
                throw new InvalidOperationException("");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="source"></param>
        /// <param name="targetType"></param>
        /// <param name="sourceType"></param>
        /// <returns></returns>
        public object Map(object source, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type targetType, Type sourceType)
        {
            object? target = null;

            target = Activator.CreateInstance(targetType);

            return mapper.Map(target!, source, targetType, sourceType);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="TTarget"></typeparam>
        /// <param name="sources"></param>
        /// <returns></returns>
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
        /// 
        /// </summary>
        /// <typeparam name="TTarget"></typeparam>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="sources"></param>
        /// <returns></returns>
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
        public bool IsMatch(Type targetType, Type sourceType)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return profile.TargetType == targetType && profile.SourceType == sourceType;
        }
    }
}

