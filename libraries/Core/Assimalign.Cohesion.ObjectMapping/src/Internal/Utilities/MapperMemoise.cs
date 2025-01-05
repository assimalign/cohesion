using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Assimalign.Extensions.Mapping.Internal;


internal static class MapperMemoise<TIn, TOut>
{
    private static IDictionary<TIn, TOut> cache;

    static MapperMemoise()
    {
        cache ??= new ConcurrentDictionary<TIn, TOut>();
    }

    /// <summary>
    /// This invocation the invocation of delegates
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Func<TIn, TOut> Invoke(Func<TIn, TOut> method)
    {
        return input => cache.TryGetValue(input, out var results) ?
            results :
            cache[input] = method.Invoke(input);
    }
}

