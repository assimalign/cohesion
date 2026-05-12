using System;
using System.Collections.Generic;

namespace System.Linq;

public static class EnumerableExtensions
{
    extension<T>(IEnumerable<T> enumerable)
    {
        /// <summary>
        /// Checks if at least one value in <paramref name="values"/> is contained in <paramref name="enumerable"/>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="values"></param>
        /// <returns></returns>
        public bool ContainsAny(IEnumerable<T> values)
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
        /// <param name="values"></param>
        /// <param name="found"></param>
        /// <returns></returns>
        public bool ContainsAny(IEnumerable<T> values, out T? found)
        {
            found = default;

            if (enumerable is string str)
            {

            }

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



}
