using System;
using System.Collections.Generic;
using System.IO.Enumeration;

namespace System.Linq;

public static class EnumerableExtensions
{
    /// <summary>
    /// Checks if at least one value in <paramref name="values"/> is contained in <paramref name="enumerable"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="values"></param>
    /// <returns></returns>
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

    /// <summary>
    ///  Checks if at least one value in <paramref name="values"/> is contained in <paramref name="enumerable"/>
    /// and returns the first one <paramref name="found"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="values"></param>
    /// <param name="found"></param>
    /// <returns></returns>
    public static bool ContainsAny<T>(this IEnumerable<T> enumerable, IEnumerable<T> values, out T? found)
    {
        found = default;

        foreach (var value in values)
        {
            if (enumerable.Contains(value))
            {
                found = value;
                return true;
            }
        }
        return false;
    }
}
