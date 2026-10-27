using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Storage.Internal;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageRecordFreeBytesTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Allocation measurement: the test host excludes background-GC counter artifacts")]
    public void TestHost_AllocationMeasurement_ShouldDisableBackgroundGc()
    {
        GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? enabled).ShouldBeTrue();
        enabled.ShouldBeOfType<bool>().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free-byte candidates: changing and removing pages agrees with a brute-force oracle (#1288)")]
    public void TryGet_ChangingOwnersAndCapacities_ShouldReturnAFittingMaximum()
    {
        var map = new StorageRecordFreeBytes();
        var expected = new Dictionary<long, (ulong Owner, int Capacity)>();
        var random = new Random(1288);
        for (int step = 0; step < 2000; step++)
        {
            long page = random.Next(128);
            if (step % 7 == 0) { map.Remove(page); expected.Remove(page); }
            else
            {
                ulong owner = (ulong)random.Next(3);
                int capacity = random.Next(-4, Units.SlottedPage.MaxRecordSize + 1);
                map.Register(owner, page, capacity);
                expected[page] = (owner, capacity);
            }
            for (ulong owner = 0; owner < 3; owner++)
            {
                foreach (int minimum in new[] { 0, 500, 4000, 8000 })
                {
                    var matches = expected.Where(pair => pair.Value.Owner == owner && pair.Value.Capacity >= minimum).ToArray();
                    bool found = map.TryGet(owner, minimum, null, out long actual);
                    found.ShouldBe(matches.Length > 0);
                    if (!found) { continue; }
                    expected[actual].Owner.ShouldBe(owner);
                    expected[actual].Capacity.ShouldBe(matches.Max(pair => pair.Value.Capacity));
                    if (matches.Length > 1)
                    {
                        map.TryGet(owner, minimum, new HashSet<long> { actual }, out long alternative).ShouldBeTrue();
                        alternative.ShouldNotBe(actual);
                        expected[alternative].Owner.ShouldBe(owner);
                        expected[alternative].Capacity.ShouldBeGreaterThanOrEqualTo(minimum);
                    }
                    var excluded = matches.Select(pair => pair.Key).ToHashSet();
                    map.TryGet(owner, minimum, excluded, out _).ShouldBeFalse();
                }
            }
        }
    }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Storage] - Free-byte candidates: updates reuse page nodes without allocating (#1288)")]
    public void Adjust_RegisteredPages_ShouldAllocateNothing()
    {
        var map = new StorageRecordFreeBytes();
        for (long page = 1; page <= 64; page++) { map.Register(7, page, 4000); }
        for (int i = 0; i < 1000; i++)
        {
            long page = (i % 64) + 1;
            map.Adjust(page, 1, false);
            map.Adjust(page, -1, false);
            map.TryGet(7, 4000, null, out _);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
        {
            long page = (i % 64) + 1;
            map.Adjust(page, 1, false);
            map.Adjust(page, -1, false);
            map.TryGet(7, 4000, null, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0);
    }
#endif
}
