using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Diagnostics")]
internal sealed class HttpLoggingEventSource : EventSource
{
    public static readonly HttpLoggingEventSource Log = new();

    private readonly FailureThrottle _writeFailureThrottle = new();
    private readonly FailureThrottle _retentionFailureThrottle = new();
    private readonly FailureThrottle _entryDroppedThrottle = new();
    private readonly FailureThrottle _addressResolverFailureThrottle = new();

    private long _failedAccessLogWrites;
    private long _droppedHttpLogEntries;
    private PollingCounter? _failedAccessLogWritesCounter;
    private PollingCounter? _droppedHttpLogEntriesCounter;

    private HttpLoggingEventSource()
    {
    }

    internal long FailedAccessLogWrites => Interlocked.Read(ref _failedAccessLogWrites);

    internal long DroppedHttpLogEntries => Interlocked.Read(ref _droppedHttpLogEntries);

    [NonEvent]
    public void FileOpened(string path, int sequence, long existingBytes)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            AccessLogFileOpened(path, sequence, existingBytes);
        }
    }

    [NonEvent]
    public void WriteFailed(string operation, Exception exception)
    {
        // Buffered entries can be lost on flush or close. This total deliberately counts
        // failed Write calls, which the caller reports once, rather than guessing lost entries.
        if (operation == "Write")
        {
            Interlocked.Increment(ref _failedAccessLogWrites);
        }

        if (IsEnabled(EventLevel.Error, EventKeywords.None)
            && _writeFailureThrottle.TryAcquire(out int suppressedCount))
        {
            AccessLogWriteFailed(operation, exception.GetType().FullName ?? string.Empty,
                exception.Message, suppressedCount);
        }
    }

    [NonEvent]
    public void RetentionFailed(Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && _retentionFailureThrottle.TryAcquire(out int suppressedCount))
        {
            AccessLogRetentionFailed(exception.GetType().FullName ?? string.Empty,
                exception.Message, suppressedCount);
        }
    }

    [NonEvent]
    public void EntryDropped(Exception exception)
    {
        Interlocked.Increment(ref _droppedHttpLogEntries);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && _entryDroppedThrottle.TryAcquire(out int suppressedCount))
        {
            HttpLogEntryDropped(exception.GetType().FullName ?? string.Empty, suppressedCount);
        }
    }

    [NonEvent]
    public void AddressResolverFailed(Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && _addressResolverFailureThrottle.TryAcquire(out int suppressedCount))
        {
            ClientAddressResolverFailed(exception.GetType().FullName ?? string.Empty, suppressedCount);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Access log file {0} opened at sequence {1} with {2} existing bytes")]
    private void AccessLogFileOpened(string path, int sequence, long existingBytes)
        => WriteEvent(1, path, sequence, existingBytes);

    [Event(2, Level = EventLevel.Error, Message = "Access log {0} failed: {1}: {2}; {3} suppressed")]
    private void AccessLogWriteFailed(string operation, string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(2, operation, exceptionType, exceptionMessage, suppressedCount);

    [Event(3, Level = EventLevel.Warning, Message = "Access log retention failed: {0}: {1}; {2} suppressed")]
    private void AccessLogRetentionFailed(string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(3, exceptionType, exceptionMessage, suppressedCount);

    [Event(4, Level = EventLevel.Warning, Message = "HTTP log entry dropped: {0}; {1} suppressed")]
    private void HttpLogEntryDropped(string exceptionType, int suppressedCount)
        => WriteEvent(4, exceptionType, suppressedCount);

    [Event(5, Level = EventLevel.Warning, Message = "Client address resolver failed: {0}; {1} suppressed")]
    private void ClientAddressResolverFailed(string exceptionType, int suppressedCount)
        => WriteEvent(5, exceptionType, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _failedAccessLogWritesCounter ??= new PollingCounter("failed-access-log-writes", this, () => FailedAccessLogWrites)
            {
                DisplayName = "Failed access log writes"
            };
            _droppedHttpLogEntriesCounter ??= new PollingCounter("dropped-http-log-entries", this, () => DroppedHttpLogEntries)
            {
                DisplayName = "Dropped HTTP log entries"
            };
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("FailureThrottle")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class FailureThrottle
    {
        private readonly Lock _lock = new();
        private long _lastTimestamp;
        private int _suppressedCount;
        private bool _hasEmitted;

        public bool TryAcquire(out int suppressedCount)
        {
            lock (_lock)
            {
                long timestamp = Stopwatch.GetTimestamp();
                if (_hasEmitted && timestamp - _lastTimestamp < Stopwatch.Frequency)
                {
                    _suppressedCount++;
                    suppressedCount = 0;
                    return false;
                }

                _hasEmitted = true;
                _lastTimestamp = timestamp;
                suppressedCount = _suppressedCount;
                _suppressedCount = 0;
                return true;
            }
            // The caller emits after leaving this lock: listeners run synchronously and may
            // themselves encounter another logging failure.
        }

        private sealed class DebugView
        {
            public DebugView(FailureThrottle value)
            {
            }
        }
    }
}
