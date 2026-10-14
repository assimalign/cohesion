using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Sessions")]
internal sealed class WebSessionEventSource : EventSource
{
    public static readonly WebSessionEventSource Log = new();

    private long _orphanedSessions;
    private long _lostSessions;
    private long _nextLossReport;
    private int _suppressedLosses;
    private PollingCounter? _orphanedSessionsCounter;
    private PollingCounter? _lostSessionsCounter;

    private WebSessionEventSource() { }

    internal long OrphanedSessions => Interlocked.Read(ref _orphanedSessions);
    internal long LostSessions => Interlocked.Read(ref _lostSessions);

    [NonEvent]
    public void SessionOrphaned(IHttpContext context)
    {
        Interlocked.Increment(ref _orphanedSessions);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            SessionOrphaned("ResponseStarted", context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Session orphaned ({0}) for request {1}")]
    private void SessionOrphaned(string reason, string requestId)
        => WriteEvent(1, reason, requestId);

    [NonEvent]
    public void SessionStateLost(IHttpContext context)
    {
        Interlocked.Increment(ref _lostSessions);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && TryReportLoss(out int suppressedCount))
        {
            SessionStateLost("ResponseStarted", context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty, suppressedCount);
        }
    }

    [Event(2, Level = EventLevel.Warning, Message = "Session state lost ({0}) for request {1}; {2} earlier losses suppressed")]
    private void SessionStateLost(string reason, string requestId, int suppressedCount)
        => WriteEvent(2, reason, requestId, suppressedCount);

    [NonEvent]
    private bool TryReportLoss(out int suppressedCount)
    {
        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref _nextLossReport);
        if (now < next || Interlocked.CompareExchange(ref _nextLossReport, now + Stopwatch.Frequency, next) != next)
        {
            Interlocked.Increment(ref _suppressedLosses);
            suppressedCount = 0;
            return false;
        }

        suppressedCount = Interlocked.Exchange(ref _suppressedLosses, 0);
        return true;
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _orphanedSessionsCounter ??= new PollingCounter("orphaned-sessions", this, () => OrphanedSessions)
            {
                DisplayName = "Orphaned sessions",
            };
            _lostSessionsCounter ??= new PollingCounter("lost-sessions", this, () => LostSessions)
            {
                DisplayName = "Lost sessions",
            };
        }
    }
}
