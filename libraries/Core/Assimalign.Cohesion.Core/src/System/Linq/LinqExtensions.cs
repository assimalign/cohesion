using System;
using System.Collections.Generic;

namespace System.Linq;

public static class LinqExtensions
{
    public static bool ContainsAny<T>(this IEnumerable<T> enumerable, IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            if (enumerable.Contains(value))
            {
                return true;
            }
        }
        return false;
    }
}
