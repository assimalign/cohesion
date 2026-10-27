using System;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public sealed class AllocationMeasurementTests
{
    [Fact(DisplayName = "Cohesion Test [Transactions] - Allocation measurement: the test host excludes background-GC counter artifacts")]
    public void TestHost_AllocationMeasurement_ShouldDisableBackgroundGc()
    {
        GC.GetConfigurationVariables().TryGetValue("ConcurrentGC", out object? enabled).ShouldBeTrue();
        enabled.ShouldBeOfType<bool>().ShouldBeFalse();
    }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Transactions] - Allocation measurement: a real allocation remains visible")]
    public void Counter_ExplicitAllocation_ShouldRemainObservable()
    {
        GC.KeepAlive(Allocate());
        long before = GC.GetAllocatedBytesForCurrentThread();
        object value = Allocate();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(value);
        allocated.ShouldBeGreaterThan(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Allocate() => new byte[37];
#endif
}
