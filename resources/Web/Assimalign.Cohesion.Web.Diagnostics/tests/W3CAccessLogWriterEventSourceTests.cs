using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Diagnostics.Internal;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

[Collection(nameof(HttpLoggingEventSourceCollection))]
public sealed class W3CAccessLogWriterEventSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-writer-events", Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: failed Write calls remain exact while events are suppressed")]
    public async Task Write_WhenStreamFails_ShouldCountEachCallAndPreserveCompositeIsolation()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var failure = new IOException("write-" + Guid.NewGuid().ToString("N"));
        using var stream = new FailingAccessLogStream { WriteFailure = failure };
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.Zero);
        using var failingProvider = new WriterLoggerProvider(writer);
        using var recordingProvider = new RecordingLoggerProvider();
        using ILoggerFactory factory = new LoggerFactoryBuilder()
            .AddProvider(failingProvider).AddProvider(recordingProvider).Build();
        ILogger logger = factory.Create("access");
        long before = source.FailedAccessLogWrites;

        try
        {
            // Act
            for (int i = 0; i < 3; i++)
            {
                Should.NotThrow(() => logger.Log(CreateEntry()));
            }

            // Assert
            source.FailedAccessLogWrites.ShouldBe(before + 3);
            recordingProvider.Entries.Count.ShouldBe(3);
            AssertWriteFailure(Failures(recorder, failure.Message).ShouldHaveSingleItem(), "Write", failure);

            // Act: the next emitted event accounts for both suppressed calls.
            await WaitForThrottleAsync();
            Should.Throw<IOException>(() => writer.Write(CreateEntry())).ShouldBeSameAs(failure);

            // Assert
            source.FailedAccessLogWrites.ShouldBe(before + 4);
            EventWrittenEventArgs[] failures = Failures(recorder, failure.Message);
            failures.Length.ShouldBe(2);
            AssertWriteFailure(failures[1], "Write", failure);
            failures[1].Payload![3].ShouldBe(2);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            stream.WriteFailure = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: explicit Flush failure is reported without counting a Write")]
    public async Task Flush_WhenStreamFails_ShouldReportWithoutIncrementingFailedWrites()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var failure = new IOException("explicit-flush-" + Guid.NewGuid().ToString("N"));
        using var stream = new FailingAccessLogStream { FlushFailure = failure };
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.FromHours(1));
        writer.Write(CreateEntry());
        long before = source.FailedAccessLogWrites;

        try
        {
            // Act
            Should.Throw<IOException>(writer.Flush).ShouldBeSameAs(failure);

            // Assert
            AssertWriteFailure(Failures(recorder, failure.Message).ShouldHaveSingleItem(), "Flush", failure);
            source.FailedAccessLogWrites.ShouldBe(before);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            stream.FlushFailure = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: timer Flush failure is reported without counting a Write")]
    public async Task TimerFlush_WhenStreamFails_ShouldReportWithoutIncrementingFailedWrites()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var failure = new IOException("timer-flush-" + Guid.NewGuid().ToString("N"));
        using var stream = new FailingAccessLogStream { FlushFailure = failure };
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.FromMilliseconds(50));
        long before = source.FailedAccessLogWrites;

        try
        {
            // Act
            writer.Write(CreateEntry());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Failures(recorder, failure.Message).Length == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
            }

            // Assert
            AssertWriteFailure(Failures(recorder, failure.Message).First(), "Flush", failure);
            source.FailedAccessLogWrites.ShouldBe(before);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            stream.FlushFailure = null;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: Close failures are reported without counting a Write")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WhenFlushOrDisposeFails_ShouldReportWithoutIncrementingFailedWrites(bool failDispose)
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var failure = new IOException("close-" + Guid.NewGuid().ToString("N"));
        var stream = new FailingAccessLogStream();
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.FromHours(1));
        writer.Write(CreateEntry());
        if (failDispose)
        {
            stream.DisposeFailure = failure;
        }
        else
        {
            // StreamWriter flushes once explicitly and again during Dispose. Only the first
            // flush fails so the close-flush policy can be tested independently of disposal.
            stream.FlushFailure = failure;
            stream.ClearFlushFailureAfterThrow = true;
        }
        long before = source.FailedAccessLogWrites;

        try
        {
            // Act
            if (failDispose)
            {
                Should.Throw<IOException>(writer.Dispose).ShouldBeSameAs(failure);
            }
            else
            {
                Should.NotThrow(writer.Dispose);
            }

            // Assert
            AssertWriteFailure(Failures(recorder, failure.Message).ShouldHaveSingleItem(), "Close", failure);
            source.FailedAccessLogWrites.ShouldBe(before);
            stream.WasDisposed.ShouldBeTrue();
            Should.NotThrow(writer.Dispose);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            stream.FlushFailure = null;
            stream.DisposeFailure = null;
            stream.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: retention enumeration failure remains best effort and reports its payload")]
    public async Task Write_WhenRetentionDirectoryDisappears_ShouldReportAndComplete()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        using var stream = new FailingAccessLogStream();
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.Zero, retentionLimit: 1, removeDirectory: true);
        long before = source.FailedAccessLogWrites;

        // Act
        Should.NotThrow(() => writer.Write(CreateEntry()));

        // Assert
        EventWrittenEventArgs retained = recorder.Events.Where(entry => entry.EventId == 3 &&
                entry.Payload?.ElementAtOrDefault(1) is string message && message.Contains(_directory, StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        retained.EventName.ShouldBe("AccessLogRetentionFailed");
        retained.Level.ShouldBe(EventLevel.Warning);
        retained.PayloadNames.ShouldNotBeNull().ShouldBe(new[] { "exceptionType", "exceptionMessage", "suppressedCount" });
        retained.Payload.ShouldNotBeNull()[0].ShouldBe(typeof(DirectoryNotFoundException).FullName);
        retained.Payload![1].ShouldBeOfType<string>().ShouldContain(_directory, Case.Sensitive);
        retained.Payload![2].ShouldBeOfType<int>().ShouldBeGreaterThanOrEqualTo(0);
        source.FailedAccessLogWrites.ShouldBe(before);
        stream.Length.ShouldBeGreaterThan(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: event listeners can acquire the writer lock from another thread")]
    [InlineData("Open", 1)]
    [InlineData("Write", 2)]
    [InlineData("Flush", 2)]
    [InlineData("Close", 2)]
    [InlineData("Retention", 3)]
    public async Task Write_WhenEventListenerReentersFromAnotherThread_ShouldRaiseOutsideLock(string operation, int eventId)
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        using var stream = new FailingAccessLogStream();
        using W3CAccessLogWriter writer = CreateWriter(
            stream,
            operation == "Write" ? TimeSpan.Zero : TimeSpan.FromHours(1),
            retentionLimit: operation == "Retention" ? 1 : null,
            removeDirectory: operation == "Retention");
        if (operation is "Flush" or "Close")
        {
            writer.Write(CreateEntry());
        }
        var failure = new IOException("outside-lock-" + Guid.NewGuid().ToString("N"));
        if (operation == "Write")
        {
            stream.WriteFailure = failure;
        }
        else if (operation == "Flush")
        {
            stream.FlushFailure = failure;
        }
        else if (operation == "Close")
        {
            stream.DisposeFailure = failure;
        }
        using var listener = new WriterLockProbeListener(source, eventId, () =>
        {
            try
            {
                writer.Flush();
            }
            catch (IOException)
            {
                // The probe only verifies that another thread can enter the writer.
            }
        });

        try
        {
            // Act
            if (operation == "Flush")
            {
                Should.Throw<IOException>(writer.Flush);
            }
            else if (operation == "Close")
            {
                Should.Throw<IOException>(writer.Dispose);
            }
            else if (operation == "Write")
            {
                Should.Throw<IOException>(() => writer.Write(CreateEntry()));
            }
            else
            {
                writer.Write(CreateEntry());
            }

            // Assert
            Task probe = listener.ProbeTask.ShouldNotBeNull();
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
            listener.CompletedInsideCallback.ShouldBeTrue();
            recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            stream.WriteFailure = null;
            stream.FlushFailure = null;
            stream.DisposeFailure = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: repeated retention failures report suppressed occurrences")]
    public async Task Write_WhenRetentionRepeatedlyFails_ShouldReportSuppressedCount()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        long before = source.FailedAccessLogWrites;

        // Act
        for (int i = 0; i < 3; i++)
        {
            WriteWithMissingRetentionDirectory();
        }

        // Assert
        RetentionFailures().ShouldHaveSingleItem();
        source.FailedAccessLogWrites.ShouldBe(before);

        // Act: the next emitted event accounts for the two suppressed failures.
        await WaitForThrottleAsync();
        WriteWithMissingRetentionDirectory();

        // Assert
        EventWrittenEventArgs[] failures = RetentionFailures();
        failures.Length.ShouldBe(2);
        failures[1].EventName.ShouldBe("AccessLogRetentionFailed");
        failures[1].Level.ShouldBe(EventLevel.Warning);
        failures[1].PayloadNames.ShouldNotBeNull().ShouldBe(new[] { "exceptionType", "exceptionMessage", "suppressedCount" });
        failures[1].Payload.ShouldNotBeNull()[0].ShouldBe(typeof(DirectoryNotFoundException).FullName);
        failures[1].Payload![2].ShouldBe(2);
        source.FailedAccessLogWrites.ShouldBe(before);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);

        void WriteWithMissingRetentionDirectory()
        {
            using var stream = new FailingAccessLogStream();
            using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.Zero, retentionLimit: 1, removeDirectory: true);
            Should.NotThrow(() => writer.Write(CreateEntry()));
        }

        EventWrittenEventArgs[] RetentionFailures()
            => recorder.Events.Where(entry => entry.EventId == 3 &&
                entry.Payload?.ElementAtOrDefault(1) is string message && message.Contains(_directory, StringComparison.Ordinal)).ToArray();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - Writer: failed Write calls remain exact without a listener")]
    public void Write_WhenStreamFailsWithoutListener_ShouldCountEachCall()
    {
        // Arrange
        HttpLoggingEventSource source = HttpLoggingEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        var failure = new IOException("unobserved-write-" + Guid.NewGuid().ToString("N"));
        using var stream = new FailingAccessLogStream { WriteFailure = failure };
        using W3CAccessLogWriter writer = CreateWriter(stream, TimeSpan.Zero);
        long before = source.FailedAccessLogWrites;

        try
        {
            // Act
            for (int i = 0; i < 3; i++)
            {
                Should.Throw<IOException>(() => writer.Write(CreateEntry())).ShouldBeSameAs(failure);
            }

            // Assert
            source.FailedAccessLogWrites.ShouldBe(before + 3);
            source.IsEnabled().ShouldBeFalse();
        }
        finally
        {
            stream.WriteFailure = null;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private W3CAccessLogWriter CreateWriter(
        FailingAccessLogStream stream,
        TimeSpan flushInterval,
        int? retentionLimit = null,
        bool removeDirectory = false)
        => new(
            _directory,
            "access",
            AccessLogFormat.Common,
            fileSizeLimit: null,
            retainedFileCountLimit: retentionLimit,
            flushInterval,
            streamFactory: _ =>
            {
                if (removeDirectory)
                {
                    Directory.Delete(_directory);
                }

                return stream;
            });

    private static LoggerEntry CreateEntry()
        => new(
            LogLevel.Information,
            "access",
            "exchange",
            attributes: new Dictionary<string, object?>
            {
                [HttpLoggingAttributes.Event] = HttpLoggingAttributes.EventExchange,
                [HttpLoggingAttributes.RequestMethod] = "GET",
                [HttpLoggingAttributes.ResponseStatusCode] = 200,
            },
            timestamp: new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));

    private static Task WaitForThrottleAsync() => Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);

    private static EventWrittenEventArgs[] Failures(EventSourceRecorder recorder, string message)
        => recorder.Events.Where(entry => entry.EventId == 2 && entry.Payload?.ElementAtOrDefault(2) as string == message).ToArray();

    private static void AssertWriteFailure(EventWrittenEventArgs entry, string operation, IOException failure)
    {
        entry.EventName.ShouldBe("AccessLogWriteFailed");
        entry.Level.ShouldBe(EventLevel.Error);
        entry.PayloadNames.ShouldNotBeNull().ShouldBe(new[] { "operation", "exceptionType", "exceptionMessage", "suppressedCount" });
        entry.Payload.ShouldNotBeNull().Take(3).ShouldBe(new object?[] { operation, typeof(IOException).FullName, failure.Message });
        entry.Payload![3].ShouldBeOfType<int>().ShouldBeGreaterThanOrEqualTo(0);
    }
}
