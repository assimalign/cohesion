using System;
using System.Collections.Generic;
using System.Threading;
using Shouldly;

namespace Assimalign.Cohesion.ObjectPool.Tests;

public class CancellationTokenSourcePoolTests
{
    public static IEnumerable<object[]> TimeProviders()
    {
        yield return new object[] { TimeProvider.System };
        //yield return new object[] { new FakeTimeProvider() };
    }

    [Fact]
    public void TestArgumentValidation()
    {
        var pool = new CancellationTokenSourcePool(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(TimeSpan.Zero));
        
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            pool.Rent(TimeSpan.FromMilliseconds(-2));
        });
        
        exception.Message.ShouldStartWith("Invalid delay specified.");
        exception.ActualValue.ShouldBe(TimeSpan.FromMilliseconds(-2));

        CancellationTokenSource cancellationTokenSource = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);
        cancellationTokenSource.ShouldNotBeNull();
    }

    [Theory]
    [MemberData(nameof(TimeProviders))]
    public void TestRentReturnReusableEnsureProperBehavior(object timeProvider)
    {
        CancellationTokenSourcePool pool = new CancellationTokenSourcePool(GetTimeProvider(timeProvider));

        // Rent and return Cancellation Token Source
        CancellationTokenSource cancellationTokenSource1 = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);
        pool.Return(cancellationTokenSource1);

        // Rent another Cancellation Token Source. Should expect the same token rented from previous operation
        CancellationTokenSource cancellationTokenSource2 = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);

        if (timeProvider == TimeProvider.System)
        {
            cancellationTokenSource2.ShouldBeSameAs(cancellationTokenSource1);
        }
        else
        {
            cancellationTokenSource2.ShouldNotBeSameAs(cancellationTokenSource1);
        }
    }


    [Theory]
    [MemberData(nameof(TimeProviders))]
    public void RentReturn_NotReusable_EnsureProperBehavior(object timeProvider)
    {
        var pool = new CancellationTokenSourcePool(GetTimeProvider(timeProvider));
        var cts = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);
        cts.Cancel();
        pool.Return(cts);

        Should.Throw<ObjectDisposedException>(() => cts.Token);

        var cts2 = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);

        Should.NotThrow(() => cts2.Token);
    }


    //[Theory]
    //[MemberData(nameof(TimeProviders))]
    //public async Task Rent_Cancellable_EnsureCancelled(object timeProvider)
    //{
    //    var pool = new CancellationTokenSourcePool(GetTimeProvider(timeProvider));
    //    var cts = pool.Rent(TimeSpan.FromMilliseconds(1));

    //    if (timeProvider is FakeTimeProvider fakeTimeProvider)
    //    {
    //        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));
    //    }

    //    await Task.Delay(100, TestCancellation.Token);

    //    await TestUtilities.AssertWithTimeoutAsync(() => cts.IsCancellationRequested.ShouldBeTrue());
    //}

    //[Theory]
    //[MemberData(nameof(TimeProviders))]
    //public async Task Rent_NotCancellable_EnsureNotCancelled(object timeProvider)
    //{
    //    var pool = new CancellationTokenSourcePool(GetTimeProvider(timeProvider));
    //    var cts = pool.Rent(System.Threading.Timeout.InfiniteTimeSpan);

    //    await Task.Delay(20, TestCancellation.Token);

    //    cts.IsCancellationRequested.ShouldBeFalse();
    //}

    private static TimeProvider GetTimeProvider(object timeProvider) => (TimeProvider)timeProvider;
}
