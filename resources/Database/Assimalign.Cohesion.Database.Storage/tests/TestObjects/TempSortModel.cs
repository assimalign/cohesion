using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal static class TempSortModel
{
    public static (long Data, long LargestMerge, long Writes) Measure(int count, int grant, int framed, int fanIn)
    {
        int workspace = grant - 32768, arena = (int)(workspace * 4L / 5) & ~3;
        int perRun = Math.Min(arena / framed, (workspace - arena) / 4);
        var remaining = new List<long>();
        for (int rows = count; rows > 0; rows -= Math.Min(perRun, rows)) { remaining.Add(Math.Min(perRun, rows)); }
        long writes = count, largest = 0;
        while (remaining.Count > fanIn)
        {
            remaining.Sort(); int remainder = (remaining.Count - fanIn) % (fanIn - 1);
            int take = remainder == 0 ? fanIn : remainder + 1;
            long merged = remaining.Take(take).Sum(); remaining.RemoveRange(0, take); remaining.Add(merged);
            writes += merged; largest = Math.Max(largest, Pages(merged * (framed + 4L)));
        }
        return (Pages(count * (framed + 4L)), largest, writes);
    }
    private static long Pages(long bytes) => (bytes + StorageTempRun.PayloadSize - 1) / StorageTempRun.PayloadSize * 8192;
}
