using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.StaticFiles.Internal;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.StaticFiles.Tests;

[CollectionDefinition("Static files events", DisableParallelization = true)]
public sealed class StaticFilesEventSourceCollection { }

[Collection("Static files events")]
public sealed class StaticFilesEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(StaticFilesEventSource)).ShouldBe("Assimalign.Cohesion.Web.StaticFiles");
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(StaticFilesEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Mount lifecycle: Branch and fallback compose once before requests")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Build_BranchOrFallback_ShouldReportMountOnce(bool fallback, bool available)
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string contentRoot = Path.Combine(Path.GetTempPath(), "cohesion-static-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            if (available)
            {
                string webRoot = Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot")).FullName;
                File.WriteAllText(Path.Combine(webRoot, "index.html"), "home");
            }

            using EventSourceRecorder recorder = new(StaticFilesEventSource.Log, EventLevel.Verbose);
            await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
            {
                ContentRootPath = FileSystemPath.Parse(contentRoot),
            });
            factory.Builder.Services.AddRouting();
            if (fallback)
            {
                factory.Application.UseRouting();
                factory.Application.MapFallbackToFile("index.html");
            }
            else
            {
                // Registering builds the middleware; the web root is mounted, and reported, only when the
                // nested branch composes it.
                factory.Builder.Services.AddStaticFiles(files => files.Options.RequestPath = new HttpPath("/assets/"));
                factory.Application.Map("/branch", branch => branch.UseWhen(_ => true,
                    nested => nested.UseStaticFiles()));
            }

            string expectedRoot = factory.Application.Context.WebRootPath?.ToString() ?? string.Empty;
            string consumer = fallback ? "MapFallbackToFile" : "UseStaticFiles";
            recorder.Events.ShouldBeEmpty();

            // Act — mounting occurs on pipeline build, before the first request is dispatched.
            await factory.StartAsync(timeout.Token);
            EventWrittenEventArgs mounted = recorder.Events.Single(e => e.EventId == (available ? 1 : 2) && Equals(e.Payload![0], expectedRoot));
            using HttpClient client = factory.CreateClient();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using HttpResponseMessage response = await client.GetAsync(fallback ? "/dashboard" : "/branch/assets/index.html", timeout.Token);
                response.StatusCode.ShouldBe(available ? NetHttpStatusCode.OK : NetHttpStatusCode.NotFound);
            }

            // Assert
            mounted.Level.ShouldBe(available ? EventLevel.Informational : EventLevel.Warning);
            mounted.PayloadNames!.ToArray().ShouldBe(available ? new[] { "webRoot", "prefix" } : new[] { "webRoot", "consumer" });
            mounted.Payload!.ToArray().ShouldBe(new object[] { expectedRoot, available ? (fallback ? string.Empty : "/assets") : consumer });
            recorder.Events.Count(e => e.EventId == (available ? 1 : 2) && Equals(e.Payload![0], expectedRoot)).ShouldBe(1);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
            await factory.StopAsync(timeout.Token);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.StaticFiles] - Traversal: Correlates rejection without writing the requested path")]
    [InlineData("/configured/../private-path.txt", "UnsafeSegment")]
    [InlineData("/configured/private<path.txt", "InvalidPath")]
    public async Task InvokeAsync_Traversal_ShouldReportReasonAndRequestId(string path, string reason)
    {
        // Arrange
        using InMemoryFileSystem fileSystem = StaticSite.Create();
        StaticFilesMiddleware middleware = new(fileSystem, new StaticFilesOptions { RequestPath = new HttpPath("/configured") });
        TestHttpContext context = new(path, HttpMethod.Get);
        RequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        long before = StaticFilesEventSource.Log.TraversalRejections;
        using EventSourceRecorder recorder = new(StaticFilesEventSource.Log, EventLevel.Verbose);

        // Act
        await middleware.InvokeAsync(context, _ => throw new InvalidOperationException("Traversal must not pass through."));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        StaticFilesEventSource.Log.TraversalRejections.ShouldBe(before + 1);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 3 && Equals(e.Payload![2], requestId.RequestId.ToHexString()));
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "prefix", "requestId" });
        entry.Payload!.ToArray().ShouldBe(new object[] { reason, "/configured", requestId.RequestId.ToHexString() });
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>().ShouldNotContain(value => value.Contains("private", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - Lookup failure: Throttles type-only reports while counting every failure")]
    public async Task InvokeAsync_MountFailure_ShouldReportTypeAndExactSuppression()
    {
        // Arrange — expire the process-wide report interval from earlier operations.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        using FailingLookupFileSystem fileSystem = new();
        StaticFilesMiddleware middleware = new(fileSystem, new StaticFilesOptions { RequestPath = new HttpPath("/configured") });
        long before = StaticFilesEventSource.Log.MountLookupFailures;
        using EventSourceRecorder recorder = new(StaticFilesEventSource.Log, EventLevel.Verbose);

        // Act
        for (int attempt = 0; attempt < 3; attempt++)
        {
            TestHttpContext context = new("/configured/private-path.txt", HttpMethod.Get);
            await middleware.InvokeAsync(context, http =>
            {
                http.Response.StatusCode = HttpStatusCode.NotFound;
                return Task.CompletedTask;
            });
            context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Assert
        StaticFilesEventSource.Log.MountLookupFailures.ShouldBe(before + 3);
        EventWrittenEventArgs first = recorder.Events.Single(e => e.EventId == 4 && Equals(e.Payload![0], "/configured"));
        first.Level.ShouldBe(EventLevel.Warning);
        first.PayloadNames!.ToArray().ShouldBe(new[] { "prefix", "exceptionType", "suppressedCount" });
        first.Payload![1].ShouldBe(typeof(FileSystemException).FullName);

        // The response helper shares the catch, but has no configured request prefix.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        TestHttpContext helper = new("/private-helper.txt", HttpMethod.Get);
        await helper.Response.SendFileAsync(fileSystem, "private-path.txt", cancellationToken: timeout.Token);
        helper.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        EventWrittenEventArgs second = recorder.Events.Single(e => e.EventId == 4 && Equals(e.Payload![0], string.Empty));
        second.Payload![2].ShouldBe(2);
        StaticFilesEventSource.Log.MountLookupFailures.ShouldBe(before + 4);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>().ShouldNotContain(value => value.Contains("private", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.StaticFiles] - EventSource: Counters count with no listener and all publish")]
    public async Task EnableCounters_AfterUnobservedFailures_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        StaticFilesEventSource source = StaticFilesEventSource.Log;
        long traversalBefore = source.TraversalRejections;
        long lookupBefore = source.MountLookupFailures;
        using FailingLookupFileSystem fileSystem = new();
        StaticFilesMiddleware middleware = new(fileSystem, new StaticFilesOptions());

        // Act — no EventListener exists yet.
        await middleware.InvokeAsync(new TestHttpContext("/../private-path.txt", HttpMethod.Get), _ => Task.CompletedTask);
        await middleware.InvokeAsync(new TestHttpContext("/private-path.txt", HttpMethod.Get), _ => Task.CompletedTask);

        // Assert
        source.TraversalRejections.ShouldBe(traversalBefore + 1);
        source.MountLookupFailures.ShouldBe(lookupBefore + 1);
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        await recorder.WaitForCountersAsync(["traversal-rejections", "mount-lookup-failures"], timeout.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }
}
