using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Diagnostics.Internal;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

[Collection(nameof(HttpLoggingEventSourceCollection))]
public class HttpLoggingEventSourceTests : IDisposable
{
    private const string sourceName = "Assimalign.Cohesion.Web.Diagnostics";
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(1100);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-http-log-events", Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: is named for its assembly")]
    public void GetName_HttpLoggingEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(HttpLoggingEventSource));

        // Assert
        name.ShouldBe(typeof(HttpLoggingEventSource).Assembly.GetName().Name);
        name.ShouldBe(sourceName);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: generates a strict manifest")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(HttpLoggingEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain(sourceName, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: reports each opened file once with existing length")]
    public void Write_ExistingFileAndDayRoll_ShouldReportEachOpenOnce()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        string existingPath = Path.Combine(_directory, "access-20261012.log");
        File.WriteAllText(existingPath, "#existing\n", new UTF8Encoding(false));
        long existingBytes = new FileInfo(existingPath).Length;
        using var recorder = new EventSourceRecorder(HttpLoggingEventSource.Log, EventLevel.Verbose);
        using var provider = new W3CAccessLogProvider(new W3CAccessLogOptions { Directory = _directory, FlushInterval = TimeSpan.Zero });
        ILogger logger = ((ILoggerProvider)provider).Create("access");
        DateTimeOffset timestamp = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        long writesBefore = HttpLoggingEventSource.Log.FailedAccessLogWrites;

        // Act
        logger.Log(CreateExchangeEntry(timestamp));
        logger.Log(CreateExchangeEntry(timestamp));
        logger.Log(CreateExchangeEntry(timestamp.AddDays(1)));
        provider.Dispose();

        // Assert
        EventWrittenEventArgs[] opened = recorder.Events.Where(e => e.EventId == 1
            && e.Payload![0] is string path && path.StartsWith(_directory, StringComparison.Ordinal)).ToArray();
        opened.Length.ShouldBe(2);
        opened[0].EventName.ShouldBe("AccessLogFileOpened");
        opened[0].Level.ShouldBe(EventLevel.Informational);
        opened[0].PayloadNames.ShouldBe(["path", "sequence", "existingBytes"]);
        opened[0].Payload.ShouldBe(new object?[] { existingPath, 0, existingBytes });
        opened[1].Payload.ShouldBe(new object?[] { Path.Combine(_directory, "access-20261013.log"), 0, 0L });
        HttpLoggingEventSource.Log.FailedAccessLogWrites.ShouldBe(writesBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: throttles dropped entries while counting each request-view emission failure")]
    public async Task InvokeAsync_RepeatedEmitFailures_ShouldThrottleTypeOnlyEventsAndCountEveryExchangeAsync()
    {
        // Arrange
        const int burstCount = 8;
        const string secret = "request-secret-that-must-not-reach-diagnostics";
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var recorded = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = CreateLoggingMiddleware(factory, options =>
            options.Fields = HttpLoggingFields.TraceContext | HttpLoggingFields.RequestBody | HttpLoggingFields.BytesTransferred);
        using var recorder = new EventSourceRecorder(HttpLoggingEventSource.Log, EventLevel.Verbose);
        long droppedBefore = HttpLoggingEventSource.Log.DroppedHttpLogEntries;
        long writesBefore = HttpLoggingEventSource.Log.FailedAccessLogWrites;
        int completed = 0;
        await Task.Delay(_throttleInterval, cancellation.Token);

        // Act
        for (int index = 0; index < burstCount; index++)
        {
            LoggingTestContext context = CreateFaultingContext(secret);
            Stream originalRequestBody = context.Request.Body;
            await middleware.InvokeAsync(context, async view =>
            {
                view.ShouldNotBeSameAs(context);
                view.Request.HttpContext.ShouldBeSameAs(view);
                await view.Request.Body.CopyToAsync(view.Response.Body, cancellation.Token);
                completed++;
            });
            context.Request.Body.ShouldBeSameAs(originalRequestBody);
            Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()).ShouldBe(secret);
        }
        await Task.Delay(_throttleInterval, cancellation.Token);
        await middleware.InvokeAsync(CreateFaultingContext(secret), _ => Task.CompletedTask);

        // Assert
        completed.ShouldBe(burstCount);
        recorded.Entries.ShouldBeEmpty();
        EventWrittenEventArgs[] dropped = recorder.Events.Where(e => e.EventId == 4).ToArray();
        dropped.Length.ShouldBe(2);
        dropped[0].EventName.ShouldBe("HttpLogEntryDropped");
        dropped[0].Level.ShouldBe(EventLevel.Warning);
        foreach (EventWrittenEventArgs entry in dropped)
        {
            entry.PayloadNames.ShouldBe(["exceptionType", "suppressedCount"]);
            entry.Payload![0].ShouldBe(typeof(InvalidOperationException).FullName);
            entry.Payload.ShouldNotContain(secret);
        }
        dropped[1].Payload![1].ShouldBe(burstCount - 1);
        HttpLoggingEventSource.Log.DroppedHttpLogEntries.ShouldBe(droppedBefore + burstCount + 1);
        HttpLoggingEventSource.Log.FailedAccessLogWrites.ShouldBe(writesBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: emission failure preserves the application's original exception")]
    public async Task InvokeAsync_EmitFailsWhileApplicationUnwinds_ShouldPreserveApplicationExceptionAsync()
    {
        // Arrange
        var recorded = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = CreateLoggingMiddleware(factory, options => options.Fields = HttpLoggingFields.TraceContext);
        var applicationException = new IOException("application failed");
        long droppedBefore = HttpLoggingEventSource.Log.DroppedHttpLogEntries;

        // Act
        IOException thrown = await Should.ThrowAsync<IOException>(() => middleware.InvokeAsync(
            CreateFaultingContext("private request input"), _ => Task.FromException(applicationException)));

        // Assert
        thrown.ShouldBeSameAs(applicationException);
        HttpLoggingEventSource.Log.DroppedHttpLogEntries.ShouldBe(droppedBefore + 1);
        recorded.Entries.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: counts dropped entries without an enabled listener")]
    public async Task InvokeAsync_EmitFailsWithoutListener_ShouldCountEachDroppedEntryAsync()
    {
        // Arrange
        var recorded = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = CreateLoggingMiddleware(factory, options => options.Fields = HttpLoggingFields.TraceContext);
        HttpLoggingEventSource.Log.IsEnabled().ShouldBeFalse();
        long droppedBefore = HttpLoggingEventSource.Log.DroppedHttpLogEntries;

        // Act
        for (int index = 0; index < 5; index++)
        {
            await middleware.InvokeAsync(CreateFaultingContext("private request input"), _ => Task.CompletedTask);
        }

        // Assert
        HttpLoggingEventSource.Log.DroppedHttpLogEntries.ShouldBe(droppedBefore + 5);
        recorded.Entries.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: resolver failure falls back to the peer and reports only its type")]
    public async Task InvokeAsync_ClientAddressResolverFails_ShouldThrottleTypeOnlyEventsAndKeepExchangeEntriesAsync()
    {
        // Arrange
        const int burstCount = 6;
        const string secret = "spoofed-request-value-that-must-not-be-published";
        var peer = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51000);
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var recorded = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        IWebApplicationMiddleware middleware = CreateLoggingMiddleware(factory, options =>
        {
            options.Fields = HttpLoggingFields.ClientAddress;
            options.ClientAddressResolver = _ => throw new InvalidOperationException(secret);
        });
        using var recorder = new EventSourceRecorder(HttpLoggingEventSource.Log, EventLevel.Verbose);
        long droppedBefore = HttpLoggingEventSource.Log.DroppedHttpLogEntries;
        await Task.Delay(_throttleInterval, cancellation.Token);

        // Act
        for (int index = 0; index < burstCount; index++)
        {
            await middleware.InvokeAsync(new LoggingTestContext(new HttpConnectionInfo(remoteEndPoint: peer)), _ => Task.CompletedTask);
        }
        await Task.Delay(_throttleInterval, cancellation.Token);
        await middleware.InvokeAsync(new LoggingTestContext(new HttpConnectionInfo(remoteEndPoint: peer)), _ => Task.CompletedTask);

        // Assert
        EventWrittenEventArgs[] failures = recorder.Events.Where(e => e.EventId == 5).ToArray();
        failures.Length.ShouldBe(2);
        failures[0].EventName.ShouldBe("ClientAddressResolverFailed");
        failures[0].Level.ShouldBe(EventLevel.Warning);
        foreach (EventWrittenEventArgs failure in failures)
        {
            failure.PayloadNames.ShouldBe(["exceptionType", "suppressedCount"]);
            failure.Payload![0].ShouldBe(typeof(InvalidOperationException).FullName);
            failure.Payload.ShouldNotContain(secret);
        }
        failures[1].Payload![1].ShouldBe(burstCount - 1);
        recorded.Entries.Count.ShouldBe(burstCount + 1);
        foreach (ILoggerEntry entry in recorded.Entries)
        {
            entry.Attributes[HttpLoggingAttributes.ClientAddress].ShouldBe(peer.Address.ToString());
            entry.Exception.ShouldBeNull();
        }
        HttpLoggingEventSource.Log.DroppedHttpLogEntries.ShouldBe(droppedBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: forwarded diagnostics reach ordinary sinks and stay out of exchange files")]
    public async Task ForwardEventSources_DiagnosticAndExchangeEntries_ShouldPreserveExchangeOnlyFileFilterAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var recorded = new RecordingLoggerProvider();
        using var accessLog = new W3CAccessLogProvider(new W3CAccessLogOptions { Directory = _directory, FlushInterval = TimeSpan.Zero });
        using ILoggerFactory factory = new LoggerFactoryBuilder().AddProvider(recorded).AddProvider(accessLog).Build();
        var options = new EventSourceForwardingOptions();
        options.Sources.Clear();
        options.Sources.Add(sourceName);
        using IDisposable forwarding = factory.ForwardEventSources(options);
        using var recorder = new EventSourceRecorder(HttpLoggingEventSource.Log, EventLevel.Verbose);
        string message = "diagnostic-" + Guid.NewGuid().ToString("N");
        await Task.Delay(_throttleInterval, cancellation.Token);

        // Act
        HttpLoggingEventSource.Log.WriteFailed("Flush", new IOException(message));
        bool directoryCreatedByDiagnostic = Directory.Exists(_directory);
        factory.Create(sourceName + ".HttpLogging").Log(CreateExchangeEntry(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)));
        accessLog.Flush();

        // Assert
        directoryCreatedByDiagnostic.ShouldBeFalse();
        ILoggerEntry failure = recorded.Entries.Where(e => e.Category == sourceName
            && Equals(e.Attributes["EventId"], 2)).ShouldHaveSingleItem();
        failure.Level.ShouldBe(LogLevel.Error);
        failure.Attributes["operation"].ShouldBe("Flush");
        failure.Attributes["exceptionType"].ShouldBe(typeof(IOException).FullName);
        failure.Attributes["exceptionMessage"].ShouldBe(message);
        recorded.Entries.Where(e => e.Category == sourceName && Equals(e.Attributes["EventId"], 1))
            .ShouldHaveSingleItem().Attributes["path"].ShouldBe(Path.Combine(_directory, "access-20261012.log"));
        recorded.Entries.Count(e => e.Attributes.TryGetValue(HttpLoggingAttributes.Event, out object? kind)
            && Equals(kind, HttpLoggingAttributes.EventExchange)).ShouldBe(1);
        string[] files = Directory.GetFiles(_directory, "access-*.log");
        string path = files.ShouldHaveSingleItem();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => !line.StartsWith('#')).ShouldBe(1);
        text.ShouldContain("GET /event-source-exchange", Case.Sensitive);
        text.ShouldNotContain(message, Case.Sensitive);
        text.ShouldNotContain("AccessLogWriteFailed", Case.Sensitive);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Event source: publishes both failure counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishDeclaredCountersAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(_testTimeout);

        // Act
        using var recorder = new EventSourceRecorder(HttpLoggingEventSource.Log, EventLevel.LogAlways, TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(["failed-access-log-writes", "dropped-http-log-entries"], cancellation.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static IWebApplicationMiddleware CreateLoggingMiddleware(ILoggerFactory factory, Action<HttpLoggingOptions> configure)
    {
        var logging = new HttpLoggingBuilder().UseLoggerFactory(factory);
        configure(logging.Options);
        return logging.Build();
    }

    private static LoggingTestContext CreateFaultingContext(string message)
    {
        var context = new LoggingTestContext(new HttpConnectionInfo());
        context.Features.Set(new FaultingRequestIdFeature(new InvalidOperationException(message)));
        context.Request.Headers[HttpHeaderKey.ContentType] = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(message));
        return context;
    }

    private static LoggerEntry CreateExchangeEntry(DateTimeOffset timestamp)
        => new(LogLevel.Information, sourceName + ".HttpLogging", "exchange", attributes: new Dictionary<string, object?>
        {
            [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange,
            [HttpLoggingAttributes.RequestMethod] = "GET",
            [HttpLoggingAttributes.RequestPath] = "/event-source-exchange",
            [HttpLoggingAttributes.ResponseStatusCode] = 200,
        }, timestamp: timestamp);
}
