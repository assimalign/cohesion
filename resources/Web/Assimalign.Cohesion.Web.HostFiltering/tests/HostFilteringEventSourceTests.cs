using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.HostFiltering.Internal;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.HostFiltering.Tests;

[Collection("HostFiltering diagnostics")]
public sealed class HostFilteringEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(HostFilteringEventSource)).ShouldBe("Assimalign.Cohesion.Web.HostFiltering");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - EventSource: Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation of the statically named event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation of the statically named event source.")]
    public void GenerateManifest_Source_ShouldBeValid()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(HostFilteringEventSource), null, EventManifestOptions.Strict).ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.HostFiltering] - Rejection: Correlated bounded host has no port")]
    [InlineData(false, "denied.example:8443", "denied.example")]
    [InlineData(true, "denied.example:8443", "denied.example")]
    [InlineData(true, "denied.example:999999", "denied.example")]
    [InlineData(true, "denied.example:invalid", "denied.example")]
    [InlineData(true, "[::1]:999999", "::1")]
    [InlineData(true, "[::1]:invalid", "::1")]
    public async Task InvokeAsync_RejectedHost_ShouldWriteBoundedCorrelatedEvent(bool forwarded, string authority, string expectedHost)
    {
        // Arrange
        HostFilteringEventSource source = HostFilteringEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        await using WebApplicationTestFactory factory = new();
        if (forwarded)
        {
            factory.Builder.Services.AddForwardedHeaders(forwardedHeaders => forwardedHeaders.Options.Headers = ForwardedHeaderNames.XForwarded);
        }
        factory.Builder.Services.AddHostFiltering(hosts => hosts.Options.AllowedHosts.Add("allowed.example"));
        string requestId = string.Empty;
        factory.Application.Use((context, next) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            return next(context);
        });
        if (forwarded)
        {
            factory.Application.UseForwardedHeaders();
        }
        factory.Application.UseHostFiltering();
        bool reachedNext = false;
        factory.Application.Use((context, next) =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "http://allowed.example/");
        request.Headers.TryAddWithoutValidation(forwarded ? "X-Forwarded-Host" : "Host", authority).ShouldBeTrue();
        long before = source.RejectedRequests;
        using Activity ambient = new Activity("unrelated").Start();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        reachedNext.ShouldBeFalse();
        source.RejectedRequests.ShouldBe(before + 1);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 1 && (string)e.Payload![3]! == requestId);
        entry.EventName.ShouldBe("RequestRejected");
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "host", "forwarded", "requestId" });
        entry.Payload![0].ShouldBe("HostNotAllowed");
        entry.Payload[1].ShouldBe(expectedHost);
        entry.Payload[2].ShouldBe(forwarded);
        requestId.Length.ShouldBe(32);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.HostFiltering] - Rejection: Raw empty or oversized host is bounded")]
    [InlineData(0)]
    [InlineData(300)]
    public async Task InvokeAsync_RawHost_ShouldWriteBoundedReason(int hostLength)
    {
        string? authority = hostLength == 0 ? null : new string('a', hostLength) + ":8443";
        await AssertRawHostRejectionAsync(authority, hostLength == 0 ? "EmptyHost" : "HostNotAllowed", new string('a', Math.Min(hostLength, 253)));
    }

    [Theory(DisplayName = "Cohesion Test [Web.HostFiltering] - Rejection: Malformed authority never exposes a port")]
    [InlineData("denied.example:999999", "denied.example")]
    [InlineData("denied.example:invalid", "denied.example")]
    [InlineData("[::1]:999999", "::1")]
    [InlineData("[::1]:invalid", "::1")]
    [InlineData("denied.example:", "")]
    [InlineData("[::1]:", "")]
    [InlineData("[::1]junk", "")]
    [InlineData("[::1", "")]
    public async Task InvokeAsync_MalformedAuthority_ShouldWritePortFreeHost(string authority, string expectedHost)
    {
        await AssertRawHostRejectionAsync(authority, "HostNotAllowed", expectedHost);
    }

    private static async Task AssertRawHostRejectionAsync(string? authority, string expectedReason, string expectedHost)
    {
        // Arrange
        using EventSourceRecorder recorder = new(HostFilteringEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        InMemoryConnectionListener listener = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(listener));
        builder.Services.AddHostFiltering(hosts => hosts.Options.AllowedHosts.Add("allowed.example"));
        WebApplication application = builder.Build();
        string requestId = string.Empty;
        application.Use((context, next) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            return next(context);
        });
        application.UseHostFiltering();
        IWebApplicationServer server = application.Context.Servers.Single();
        await server.StartAsync(cancellation.Token);
        long before = HostFilteringEventSource.Log.RejectedRequests;
        try
        {
            // Act
            await using IDuplexConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, cancellation.Token);
            Stream stream = connection.AsStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n" + (authority is null ? string.Empty : "Host: " + authority + "\r\n") + "Connection: close\r\n\r\n"), cancellation.Token);
            await stream.FlushAsync(cancellation.Token);
            using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
            string response = await reader.ReadToEndAsync(cancellation.Token);

            // Assert
            response.ShouldStartWith("HTTP/1.1 400");
            HostFilteringEventSource.Log.RejectedRequests.ShouldBe(before + 1);
            EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 1 && (string)e.Payload![3]! == requestId);
            entry.Payload![0].ShouldBe(expectedReason);
            entry.Payload[1].ShouldBe(expectedHost);
            entry.Payload[2].ShouldBe(false);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            await server.StopAsync(new CancellationToken(canceled: true));
            await ((IAsyncDisposable)application).DisposeAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Allowed host: No event or rejection increment")]
    public async Task InvokeAsync_AllowedHost_ShouldRemainSilent()
    {
        // Arrange
        using EventSourceRecorder recorder = new(HostFilteringEventSource.Log, EventLevel.Verbose);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddHostFiltering(hosts => hosts.Options.AllowedHosts.Add("allowed.example"));
        string requestId = string.Empty;
        factory.Application.Use((context, next) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            return next(context);
        });
        factory.Application.UseHostFiltering();
        factory.Application.Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        long before = HostFilteringEventSource.Log.RejectedRequests;

        // Act
        using HttpResponseMessage response = await client.GetAsync("http://allowed.example/");

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        HostFilteringEventSource.Log.RejectedRequests.ShouldBe(before);
        recorder.Events.ShouldNotContain(e => e.EventId == 1 && (string)e.Payload![3]! == requestId);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Counters: Rejections count without listeners or allocation")]
    public void RequestRejected_DisabledSource_ShouldCountWithoutAllocating()
    {
        // Arrange — null is deliberate: disabled diagnostics must not inspect request features.
        HostFilteringEventSource source = HostFilteringEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        HttpHost host = new(new string('a', 300) + ":8443");
        for (int i = 0; i < 100; i++)
        {
            source.RequestRejected(null!, host, emptyHost: false);
        }
        long before = source.RejectedRequests;
        long allocated = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (int i = 0; i < 1000; i++)
        {
            source.RequestRejected(null!, host, emptyHost: false);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // Assert
        allocated.ShouldBe(0);
        source.RejectedRequests.ShouldBe(before + 1000);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Counters: Rejected requests publishes")]
    public async Task OnEventCommand_Enable_ShouldPublishCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(HostFilteringEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(new[] { "rejected-requests" }, cancellation.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }
}
