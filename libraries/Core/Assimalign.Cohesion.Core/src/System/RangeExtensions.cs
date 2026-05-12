namespace System;

public static class RangeExtensions
{
    extension(Range range)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public (int start, int length) GetStartLength()
        {
            var start = range.Start.Value;
            var length = range.End.Value - start;

            return (start, length);
        }
    }
    
}