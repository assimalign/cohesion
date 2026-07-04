using System;
using System.Threading;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpServerLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should expose Kestrel-parity defaults")]
    public void Defaults_ShouldMatchKestrelParity()
    {
        HttpServerLimits limits = new();

        limits.MaxRequestLineSize.ShouldBe(8 * 1024);
        limits.MaxRequestHeaderCount.ShouldBe(100);
        limits.MaxRequestHeadersTotalSize.ShouldBe(32 * 1024);
        limits.MaxRequestBodySize.ShouldBe(30_000_000);
        limits.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(130));
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should expose the limits surface on the listener options")]
    public void ListenerOptions_ShouldExposeLimits()
    {
        HttpConnectionListenerOptions options = new();

        options.Limits.ShouldNotBeNull();
        options.Limits.MaxRequestBodySize.ShouldBe(30_000_000);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should reject non-positive size limits")]
    [InlineData(0)]
    [InlineData(-1)]
    public void SizeLimits_OnNonPositive_ShouldThrow(int value)
    {
        HttpServerLimits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestLineSize = value);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestHeaderCount = value);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestHeadersTotalSize = value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should reject a negative max request body size")]
    public void MaxRequestBodySize_OnNegative_ShouldThrow()
    {
        HttpServerLimits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should allow an unbounded (null) or zero max request body size")]
    public void MaxRequestBodySize_OnNullOrZero_ShouldBeAccepted()
    {
        HttpServerLimits limits = new()
        {
            MaxRequestBodySize = null
        };
        limits.MaxRequestBodySize.ShouldBeNull();

        limits.MaxRequestBodySize = 0;
        limits.MaxRequestBodySize.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HttpServerLimits: Should reject non-positive timeouts but accept InfiniteTimeSpan")]
    public void Timeouts_Validation()
    {
        HttpServerLimits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.KeepAliveTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.RequestHeadersTimeout = TimeSpan.FromSeconds(-1));

        limits.KeepAliveTimeout = Timeout.InfiniteTimeSpan;
        limits.KeepAliveTimeout.ShouldBe(Timeout.InfiniteTimeSpan);

        limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }
}
