using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion;

public static class Memoise<TIn, TOut>
    where TIn : notnull
{
    private static IDictionary<TIn, TOut> _cache;

    static Memoise()
    {
        _cache ??= new ConcurrentDictionary<TIn, TOut>();
    }

    /// <summary>
    /// This invocation the invocation of delegates
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Func<TIn, TOut> Invoke(Func<TIn, TOut> method)
    {
        return input => _cache.TryGetValue(input, out var results) ?
            results :
            _cache[input] = method.Invoke(input);
    }
}
