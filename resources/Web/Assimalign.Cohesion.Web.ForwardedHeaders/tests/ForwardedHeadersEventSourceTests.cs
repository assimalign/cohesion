using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.ForwardedHeaders.Internal;
using Assimalign.Cohesion.Web.ForwardedHeaders.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Tests;

[Collection("ForwardedHeaders event source")]
public sealed class ForwardedHeadersEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - EventSource: Name matches the assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(ForwardedHeadersEventSource)).ShouldBe("Assimalign.Cohesion.Web.ForwardedHeaders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - EventSource: Strict manifest validates")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest inspection of the statically named source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest inspection of the statically named source.")]
    public void GenerateManifest_Source_ShouldValidateStrictly()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(ForwardedHeadersEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Resolve: Every walk stop reports one bounded correlated event")]
    [InlineData(true, "UntrustedPeer", 0)]
    [InlineData(false, "UntrustedPeer", 0)]
    [InlineData(true, "UntrustedPeer", 1)]
    [InlineData(false, "UntrustedPeer", 1)]
    [InlineData(true, "ForwardLimit", 1)]
    [InlineData(false, "ForwardLimit", 1)]
    [InlineData(false, "MalformedFor", 0)]
    [InlineData(false, "MalformedFor", 1)]
    [InlineData(true, "InvalidProto", 0)]
    [InlineData(false, "InvalidProto", 0)]
    [InlineData(true, "InvalidProto", 1)]
    [InlineData(false, "InvalidProto", 1)]
    [InlineData(true, "ImplausibleHost", 0)]
    [InlineData(false, "ImplausibleHost", 0)]
    [InlineData(true, "ImplausibleHost", 1)]
    [InlineData(false, "ImplausibleHost", 1)]
    [InlineData(true, "UnusableForwardedHeader", 0)]
    [InlineData(true, "AddresslessHop", 1)]
    [InlineData(false, "AddresslessHop", 1)]
    public void Resolve_StoppedWalk_ShouldReportOneCorrelatedEvent(bool forwarded, string reason, int hops)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ForwardedHeadersEventSource.Log, EventLevel.Verbose);
        using Activity unrelated = new Activity("unrelated").Start();
        TestHttpContext context = CreateScenario(forwarded, reason, hops);
        ForwardedHeadersResolver resolver = CreateResolver(reason == "ForwardLimit" ? 1 : null);
        long untrustedBefore = ForwardedHeadersEventSource.Log.UntrustedForwarding;
        long malformedBefore = ForwardedHeadersEventSource.Log.MalformedForwarding;

        // Act
        HttpForwardedFeature result = resolver.Resolve(context);

        // Assert
        EventWrittenEventArgs recorded = recorder.Events.Single(e => e.EventId == 1
            && Equals(e.Payload![4], context.RequestIdFeature.Id.ToHexString()));
        recorded.EventName.ShouldBe("ForwardingStopped");
        recorded.Level.ShouldBe(EventLevel.Verbose);
        recorded.PayloadNames.ShouldBe(["reason", "family", "hopsApplied", "untrustedPeer", "requestId"]);
        recorded.Payload!.ToArray().ShouldBe(new object?[]
        {
            reason, forwarded ? "Forwarded" : "X-Forwarded", hops,
            reason == "UntrustedPeer" && hops == 0 ? "198.51.100.7" : string.Empty,
            context.RequestIdFeature.Id.ToHexString(),
        });
        result.TrustedHopCount.ShouldBe(hops);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        (ForwardedHeadersEventSource.Log.UntrustedForwarding - untrustedBefore).ShouldBe(reason == "UntrustedPeer" ? 1L : 0L);
        (ForwardedHeadersEventSource.Log.MalformedForwarding - malformedBefore).ShouldBe(
            reason is "MalformedFor" or "InvalidProto" or "ImplausibleHost" or "UnusableForwardedHeader" ? 1L : 0L);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - EventSource: Both exact counters publish")]
    public async Task Counters_Enabled_ShouldPublishBothTotals()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(ForwardedHeadersEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        long untrustedBefore = ForwardedHeadersEventSource.Log.UntrustedForwarding;
        long malformedBefore = ForwardedHeadersEventSource.Log.MalformedForwarding;
        ForwardedHeadersResolver resolver = CreateResolver();

        // Act
        resolver.Resolve(CreateScenario(false, "UntrustedPeer", 0));
        resolver.Resolve(CreateScenario(false, "MalformedFor", 0));
        await recorder.WaitForCountersAsync(["untrusted-forwarding", "malformed-forwarding"], cancellation.Token);

        // Assert
        (ForwardedHeadersEventSource.Log.UntrustedForwarding - untrustedBefore).ShouldBe(1);
        (ForwardedHeadersEventSource.Log.MalformedForwarding - malformedBefore).ShouldBe(1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Resolve: Disabled events still count without reading request identity")]
    public void Resolve_NoListener_ShouldCountWithoutReadingRequestId()
    {
        // Arrange
        ForwardedHeadersEventSource.Log.IsEnabled().ShouldBeFalse();
        TestHttpContext untrusted = CreateScenario(false, "UntrustedPeer", 0);
        TestHttpContext malformed = CreateScenario(false, "MalformedFor", 0);
        long untrustedBefore = ForwardedHeadersEventSource.Log.UntrustedForwarding;
        long malformedBefore = ForwardedHeadersEventSource.Log.MalformedForwarding;
        ForwardedHeadersResolver resolver = CreateResolver();

        // Act
        resolver.Resolve(untrusted);
        resolver.Resolve(malformed);

        // Assert
        (ForwardedHeadersEventSource.Log.UntrustedForwarding - untrustedBefore).ShouldBe(1);
        (ForwardedHeadersEventSource.Log.MalformedForwarding - malformedBefore).ShouldBe(1);
        untrusted.RequestIdFeature.Reads.ShouldBe(0);
        malformed.RequestIdFeature.Reads.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Resolve: Missing, unselected, or successful forwarding stays silent")]
    [InlineData("missing")]
    [InlineData("unselected")]
    [InlineData("success-forwarded")]
    [InlineData("success-x-forwarded")]
    public void Resolve_NoStoppedWalk_ShouldRemainSilent(string scenario)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ForwardedHeadersEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = new(new IPEndPoint(IPAddress.Loopback, 52100));
        ForwardedHeaderNames selected = ForwardedHeaderNames.All;
        if (scenario == "unselected")
        {
            selected = ForwardedHeaderNames.XForwardedProto;
            context.Request.Headers[HttpHeaderKey.XForwardedFor] = "private-forwarded-value";
        }
        else if (scenario == "success-forwarded")
        {
            context.Request.Headers[HttpHeaderKey.Forwarded] = "for=203.0.113.9";
        }
        else if (scenario == "success-x-forwarded")
        {
            context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        }
        ForwardedHeadersResolver resolver = new(new ForwardedHeadersOptions { Headers = selected });
        long untrustedBefore = ForwardedHeadersEventSource.Log.UntrustedForwarding;
        long malformedBefore = ForwardedHeadersEventSource.Log.MalformedForwarding;

        // Act
        resolver.Resolve(context);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 1
            && Equals(e.Payload![4], context.RequestIdFeature.Id.ToHexString()));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        context.RequestIdFeature.Reads.ShouldBe(0);
        (ForwardedHeadersEventSource.Log.UntrustedForwarding - untrustedBefore).ShouldBe(0);
        (ForwardedHeadersEventSource.Log.MalformedForwarding - malformedBefore).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Resolve: Non-IP untrusted peers never serialize endpoint text")]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_NonIpUntrustedPeer_ShouldLeavePeerEmpty(bool dns)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ForwardedHeadersEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = new(dns ? new DnsEndPoint("private-peer.example", 443) : new FakeLocalEndPoint());
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        ForwardedHeadersResolver resolver = new(new ForwardedHeadersOptions
        {
            Headers = ForwardedHeaderNames.XForwardedFor,
            TrustLocalTransports = false,
        });

        // Act
        resolver.Resolve(context);

        // Assert
        EventWrittenEventArgs recorded = recorder.Events.Single(e => e.EventId == 1
            && Equals(e.Payload![4], context.RequestIdFeature.Id.ToHexString()));
        recorded.Payload![0].ShouldBe("UntrustedPeer");
        recorded.Payload[3].ShouldBe(string.Empty);
    }

    private static ForwardedHeadersResolver CreateResolver(int? limit = null)
        => new(new ForwardedHeadersOptions { Headers = ForwardedHeaderNames.All, ForwardLimit = limit });

    private static TestHttpContext CreateScenario(bool forwarded, string reason, int hops)
    {
        TestHttpContext context = new(new IPEndPoint(
            reason == "UntrustedPeer" && hops == 0 ? IPAddress.Parse("198.51.100.7") : IPAddress.Loopback, 52100));
        string node = reason switch
        {
            "MalformedFor" => "private-malformed-value",
            "AddresslessHop" => "unknown",
            _ => "203.0.113.9",
        };
        if (forwarded)
        {
            context.Request.Headers[HttpHeaderKey.Forwarded] = reason switch
            {
                "UnusableForwardedHeader" => "for=203.0.113.9;=private-malformed-value",
                "InvalidProto" => $"for={node};proto=gopher",
                "ImplausibleHost" => $"for={node};host=\"private.example/path\"",
                "ForwardLimit" => $"for={node}, for=127.0.0.1",
                "UntrustedPeer" when hops == 1 => $"for={node}, for=203.0.113.50",
                _ => $"for={node}",
            };
        }
        else
        {
            context.Request.Headers[HttpHeaderKey.XForwardedFor] = reason switch
            {
                "ForwardLimit" => $"{node}, 127.0.0.1",
                "UntrustedPeer" when hops == 1 => $"{node}, 203.0.113.50",
                _ => node,
            };
            if (reason == "InvalidProto")
            {
                context.Request.Headers[HttpHeaderKey.XForwardedProto] = "gopher";
            }
            if (reason == "ImplausibleHost")
            {
                context.Request.Headers[HttpHeaderKey.XForwardedHost] = "private.example/path";
            }
        }

        if (hops == 1 && reason is "MalformedFor" or "InvalidProto" or "ImplausibleHost")
        {
            if (forwarded)
            {
                context.Request.Headers[HttpHeaderKey.Forwarded] = context.Request.Headers[HttpHeaderKey.Forwarded].Value + ", for=127.0.0.1";
            }
            else
            {
                context.Request.Headers[HttpHeaderKey.XForwardedFor] = node + ", 127.0.0.1";
                if (reason == "InvalidProto")
                {
                    context.Request.Headers[HttpHeaderKey.XForwardedProto] = "gopher, https";
                }
                if (reason == "ImplausibleHost")
                {
                    context.Request.Headers[HttpHeaderKey.XForwardedHost] = "private.example/path, proxy.example";
                }
            }
        }
        return context;
    }
}
