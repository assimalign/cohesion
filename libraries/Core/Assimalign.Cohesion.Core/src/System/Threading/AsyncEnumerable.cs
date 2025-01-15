using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Threading.Tasks;

public static class AsyncEnumerable
{

    public static async IAsyncEnumerable<T> EnumerateAsync<T>(this IEnumerable<Task<T>> tasks)
    {
        var items = tasks.ToList();

        while (items.Any())
        {
            var finished = await Task.WhenAny(items);

            items.Remove(finished);

            yield return finished.Result;
        }
    }
}
