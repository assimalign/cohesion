using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Caching")]
internal sealed class OutputCacheEventSource : EventSource
{
    public static readonly OutputCacheEventSource Log = new();

    private long _declinedEntries;
    private long _nextDeclineReport;
    private int _suppressedDeclines;
    private PollingCounter? _declinedEntriesCounter;

    private OutputCacheEventSource() { }

    internal long DeclinedEntries => Interlocked.Read(ref _declinedEntries);

    [NonEvent]
    public void ResponseNotStored(IHttpContext context, OutputCacheSkipReason reason)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ResponseNotStored(reason.ToString(),
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [NonEvent]
    public void EntryDeclined(long entryBytes, long sizeLimitBytes)
    {
        Interlocked.Increment(ref _declinedEntries);
        if (!IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref _nextDeclineReport);
        if (now < next || Interlocked.CompareExchange(ref _nextDeclineReport, now + Stopwatch.Frequency, next) != next)
        {
            Interlocked.Increment(ref _suppressedDeclines);
            return;
        }

        EntryDeclined(entryBytes, sizeLimitBytes, Interlocked.Exchange(ref _suppressedDeclines, 0));
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Response not stored: {0}; route {1}; request {2}")]
    private void ResponseNotStored(string reason, string routeTemplate, string requestId)
        => WriteEvent(1, reason, routeTemplate, requestId);

    [Event(2, Level = EventLevel.Warning, Message = "Cache entry of {0} bytes declined by {1} byte limit; {2} earlier declines suppressed")]
    private void EntryDeclined(long entryBytes, long sizeLimitBytes, int suppressedCount)
        => WriteEvent(2, entryBytes, sizeLimitBytes, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _declinedEntriesCounter ??= new PollingCounter("declined-entries", this, () => DeclinedEntries) { DisplayName = "Declined output cache entries" };
        }
    }
}
