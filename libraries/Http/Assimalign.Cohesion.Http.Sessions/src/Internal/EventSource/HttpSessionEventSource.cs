using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Http.Internal;

[EventSource(Name = "Assimalign.Cohesion.Http.Sessions")]
internal sealed class HttpSessionEventSource : EventSource
{
    public static readonly HttpSessionEventSource Log = new();

    private long _loadFailures;
    private long _commitFailures;
    private long _nextLoadReport;
    private long _nextCommitReport;
    private int _suppressedLoads;
    private int _suppressedCommits;
    private PollingCounter? _loadFailuresCounter;
    private PollingCounter? _commitFailuresCounter;

    private HttpSessionEventSource() { }

    internal long LoadFailures => Interlocked.Read(ref _loadFailures);
    internal long CommitFailures => Interlocked.Read(ref _commitFailures);

    [NonEvent]
    public void StoreLoadFailed(Exception exception)
    {
        Interlocked.Increment(ref _loadFailures);
        if (IsEnabled(EventLevel.Error, EventKeywords.None) &&
            TryReport(ref _nextLoadReport, ref _suppressedLoads, out int suppressedCount))
        {
            // Deviates from event-source.md type-plus-message: #1436 forbids session data, which store messages may contain.
            StoreLoadFailed(exception.GetType().FullName ?? exception.GetType().Name, suppressedCount);
        }
    }

    [NonEvent]
    public void StoreCommitFailed(Exception exception)
    {
        Interlocked.Increment(ref _commitFailures);
        if (IsEnabled(EventLevel.Error, EventKeywords.None) &&
            TryReport(ref _nextCommitReport, ref _suppressedCommits, out int suppressedCount))
        {
            StoreCommitFailed(exception.GetType().FullName ?? exception.GetType().Name, suppressedCount);
        }
    }

    [Event(1, Level = EventLevel.Error, Message = "Session store load failed ({0}); {1} earlier failures suppressed")]
    private void StoreLoadFailed(string exceptionType, int suppressedCount)
        => WriteEvent(1, exceptionType, suppressedCount);

    [Event(2, Level = EventLevel.Error, Message = "Session store commit failed ({0}); {1} earlier failures suppressed")]
    private void StoreCommitFailed(string exceptionType, int suppressedCount)
        => WriteEvent(2, exceptionType, suppressedCount);

    [NonEvent]
    private static bool TryReport(ref long nextReport, ref int suppressed, out int suppressedCount)
    {
        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref nextReport);
        if (now < next || Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency, next) != next)
        {
            Interlocked.Increment(ref suppressed);
            suppressedCount = 0;
            return false;
        }

        suppressedCount = Interlocked.Exchange(ref suppressed, 0);
        return true;
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _loadFailuresCounter ??= new PollingCounter("store-load-failures", this, () => LoadFailures)
            {
                DisplayName = "Session store load failures",
            };
            _commitFailuresCounter ??= new PollingCounter("store-commit-failures", this, () => CommitFailures)
            {
                DisplayName = "Session store commit failures",
            };
        }
    }
}
