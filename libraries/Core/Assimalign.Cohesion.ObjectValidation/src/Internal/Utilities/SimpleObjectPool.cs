using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal static class SimpleObjectPool
{
    private static ConcurrentDictionary<Type, List<object>> pools = new();

    public static T Rent<T>(int poolSize = 20) where T : new()
    {
        var pool = pools.GetOrAdd(typeof(T), type =>
        {
            var items = new List<object>();
            for (int i = 0; i < poolSize; i++)
            {
                items.Add(new T());
            }
            return items;
        });
        if (pool.Count == 0)
        {
            return new T();
        }
        else
        {
            var item = pool[0];

            pool.RemoveAt(0);

            return (T)item;
        }
    }

    public static void Return<T>(T value)
    {
        var pool = pools[typeof(T)];
        pool.Add(value);
    }
}
