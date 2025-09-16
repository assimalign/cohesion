namespace System;

public static class StringExtensions
{
    extension(string str)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="span"></param>
        /// <returns></returns>
        public bool EndsWithAny(ReadOnlySpan<string> span)
        {
            return str.EndsWithAny(span, StringComparison.Ordinal);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="span"></param>
        /// <param name="comparison"></param>
        /// <returns></returns>
        public bool EndsWithAny(ReadOnlySpan<string> span, StringComparison comparison)
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (str.EndsWith(span[i], comparison))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="span"></param>
        /// <returns></returns>
        public bool StartsWithAny(ReadOnlySpan<string> span)
        {
            return str.StartsWithAny(span, StringComparison.Ordinal);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="span"></param>
        /// <param name="comparison"></param>
        /// <returns></returns>
        public bool StartsWithAny(ReadOnlySpan<string> span, StringComparison comparison)
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (str.StartsWith(span[i], comparison))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
