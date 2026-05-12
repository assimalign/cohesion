using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

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
            else
            {
                throw new Exception("");
            }
        }
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
        public object Map(object source, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type targetType, Type sourceType)
        {
            object? target = null;

            try
            {
                target = Activator.CreateInstance(targetType);
            }
            catch (Exception exception)
            {
                throw new MapperInstanceCreationException(targetType, exception);
            }

            return mapper.Map(target!, source, targetType, sourceType);
        }


        public TTarget Map<TTarget>(params object[] sources) where TTarget : class, new()
        {
            var target = new TTarget();

            foreach (var source in sources)
            {
                target = (TTarget)mapper.Map(target, source, target.GetType(), source.GetType());
            }

            return target;
        }

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

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="keySelector"></param>
    /// <param name="valueSelector"></param>
    /// <returns></returns>
    public static Dictionary<TKey, TValue> ToDictionary<T, TKey, TValue>(this IEnumerable<T> enumerable, Func<T, TKey> keySelector, Func<T, TValue> valueSelector)
    {
        if (enumerable is null)
        {
            return null;
        }

        var dictionary = new Dictionary<TKey, TValue>();

        foreach (var item in enumerable)
        {
            dictionary.Add(keySelector(item), valueSelector(item));
        }

        return dictionary;
    }

    // public static TOut ToReferenceType<Tin, TOut>()

}

