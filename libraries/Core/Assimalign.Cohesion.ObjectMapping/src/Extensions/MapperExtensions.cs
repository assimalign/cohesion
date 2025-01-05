using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Extensions.Mapping;

public static partial class MapperExtensions
{

    /// <summary>
    /// Sets the target maps by passing in multiple source types at once.
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <param name="mapper"></param>
    /// <param name="sources"></param>
    /// <returns></returns>
    public static TTarget Map<TTarget>(this IMapper mapper, params object[] sources)
        where TTarget : class, new()
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
    /// <param name="mapper"></param>
    /// <param name="sources"></param>
    /// <returns></returns>
    public static IEnumerable<TTarget> Map<TTarget, TSource>(this IMapper mapper, IEnumerable<TSource> sources)
        where TTarget : class, new()
    {
        var targets = new List<TTarget>();

        foreach (var source in sources)
        {
            var target = new TTarget();

            targets.Add(mapper.Map(target, source));
        }

        return targets;
    }


    /// <summary>
    /// Converts a value type to nullable
    /// </summary>
    /// <typeparam name="TStruct"></typeparam>
    /// <param name="valueType"></param>
    /// <returns></returns>
    public static TStruct? ToNullable<TStruct>(this TStruct valueType)
         where TStruct : struct, IEquatable<TStruct>
    {
        if (valueType.Equals(default(TStruct)))
        {
            return new Nullable<TStruct>();
        }
        return new Nullable<TStruct>(valueType);
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

