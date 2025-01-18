using System;
using System.Linq;
using System.Collections.Generic;

namespace System.Threading.Tasks;

public static class AsyncEnumerable
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tasks"></param>
    /// <returns></returns>
    public static async IAsyncEnumerable<T> EnumerateAsync<T>(this IEnumerable<Task<T>> tasks)
    {
        var items = tasks is ICollection<Task<T>> collection ? collection : tasks.ToList();

        while (items.Any())
        {
            var finished = await Task.WhenAny(items);

            items.Remove(finished);

            yield return finished.Result;
        }
    }
}
