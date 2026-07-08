using System;
using System.Threading;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpConnectionListenerLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Http1 limits should expose Kestrel-parity defaults")]
    public void Http1Defaults_ShouldMatchKestrelParity()
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        limits.MaxRequestLineSize.ShouldBe(8 * 1024);
        limits.MaxRequestHeaderCount.ShouldBe(100);
        limits.MaxRequestHeadersTotalSize.ShouldBe(32 * 1024);
        limits.MaxRequestBodySize.ShouldBe(30_000_000);
        limits.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(130));
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(30));

        // Kestrel MinRequestBodyDataRate / MinResponseDataRate parity: 240 octets/s, 5-second grace.
        limits.MinRequestBodyDataRate.ShouldNotBeNull();
        limits.MinRequestBodyDataRate!.BytesPerSecond.ShouldBe(240);
        limits.MinRequestBodyDataRate.GracePeriod.ShouldBe(TimeSpan.FromSeconds(5));
        limits.MinResponseDataRate.ShouldNotBeNull();
        limits.MinResponseDataRate!.BytesPerSecond.ShouldBe(240);
        limits.MinResponseDataRate.GracePeriod.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Data-rate limits should be nullable to disable the check")]
    public void DataRateLimits_ShouldBeNullableToDisable()
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MinRequestBodyDataRate = null,
            MinResponseDataRate = null,
        };

        limits.MinRequestBodyDataRate.ShouldBeNull();
        limits.MinResponseDataRate.ShouldBeNull();

        HttpMinDataRate rate = new(bytesPerSecond: 512, gracePeriod: TimeSpan.FromSeconds(2));
        limits.MinRequestBodyDataRate = rate;
        limits.MinRequestBodyDataRate.ShouldBeSameAs(rate);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Http2 limits should inherit the shared limits alongside the abuse caps")]
    public void Http2Defaults_ShouldExposeSharedAndVersionSpecificLimits()
    {
        Http2ConnectionListenerOptions.Http2Limits limits = new();

        // Shared (inherited from HttpConnectionListenerLimits).
        limits.MaxRequestBodySize.ShouldBe(30_000_000);
        limits.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(130));
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(30));

        // HTTP/2-specific abuse caps.
        limits.MaxStreamsPerConnection.ShouldBe(100);
        limits.MaxRequestHeaderListSize.ShouldBe(16 * 1024);
        limits.MaxResetStreamsPerWindow.ShouldBe(200);
        limits.MaxSettingsFramesPerWindow.ShouldBe(100);
        limits.MaxPingFramesPerWindow.ShouldBe(100);
        limits.FloodDetectionWindow.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Each version's listener options should expose its own limits")]
    public void VersionOptions_ShouldExposeLimits()
    {
        Http1ConnectionListenerOptions http1 = new();
        Http2ConnectionListenerOptions http2 = new();

        http1.Limits.ShouldNotBeNull();
        http1.Limits.MaxRequestBodySize.ShouldBe(30_000_000);
        http2.Limits.ShouldNotBeNull();
        http2.Limits.MaxRequestHeaderListSize.ShouldBe(16 * 1024);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Limits: Should reject non-positive HTTP/1.1 size limits")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Http1SizeLimits_OnNonPositive_ShouldThrow(int value)
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestLineSize = value);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestHeaderCount = value);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestHeadersTotalSize = value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Should reject a negative max request body size")]
    public void MaxRequestBodySize_OnNegative_ShouldThrow()
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Should allow an unbounded (null) or zero max request body size")]
    public void MaxRequestBodySize_OnNullOrZero_ShouldBeAccepted()
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MaxRequestBodySize = null
        };
        limits.MaxRequestBodySize.ShouldBeNull();

        limits.MaxRequestBodySize = 0;
        limits.MaxRequestBodySize.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Limits: Should reject non-positive timeouts but accept InfiniteTimeSpan")]
    public void Timeouts_Validation()
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        Should.Throw<ArgumentOutOfRangeException>(() => limits.KeepAliveTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => limits.RequestHeadersTimeout = TimeSpan.FromSeconds(-1));

        limits.KeepAliveTimeout = Timeout.InfiniteTimeSpan;
        limits.KeepAliveTimeout.ShouldBe(Timeout.InfiniteTimeSpan);

        limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }
}
