namespace System;

public static class RangeExtensions
{

    public static (int start, int length) GetStartLength(this Range range)
    {
        var start = range.Start.Value;
        var length =  range.End.Value - start;

        return (start, length);
    }
}
