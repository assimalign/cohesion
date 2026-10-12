using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.RateLimiting")]
internal sealed class RateLimitingEventSource : EventSource
{
    public static readonly RateLimitingEventSource Log = new();

    private readonly Lock _abortGate = new();
    private long _rejectedRequests;
    private PollingCounter? _rejectedRequestsCounter;
    private long _lastAbortTimestamp;
    private bool _hasReportedAbort;
    private int _suppressedAborts;

    private RateLimitingEventSource()
    {
    }

    internal long RejectedRequests => Interlocked.Read(ref _rejectedRequests);

    [NonEvent]
    public void RequestRejected(IHttpContext context, string gate, string? policyName, TimeSpan? retryAfter)
    {
        Interlocked.Increment(ref _rejectedRequests);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            RequestRejected(
                gate,
                policyName ?? string.Empty,
                retryAfter?.TotalSeconds ?? -1,
                context.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Request {4} rejected by {0} rate limit policy '{1}' (retry after {2} seconds), route '{3}'")]
    private void RequestRejected(string gate, string policyName, double retryAfterSeconds, string routeTemplate, string requestId)
        => WriteEvent(1, gate, policyName, retryAfterSeconds, routeTemplate, requestId);

    [NonEvent]
    public void RejectionAborted(IHttpContext context, string gate, string? policyName)
    {
        if (!IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            return;
        }

        lock (_abortGate)
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (_hasReportedAbort && Stopwatch.GetElapsedTime(_lastAbortTimestamp, timestamp) < TimeSpan.FromSeconds(1))
            {
                _suppressedAborts = _suppressedAborts == int.MaxValue ? int.MaxValue : _suppressedAborts + 1;
                return;
            }

            _hasReportedAbort = true;
            _lastAbortTimestamp = timestamp;
            int suppressedCount = _suppressedAborts;
            _suppressedAborts = 0;

            // Serialize delivery as well as admission, so a preempted writer cannot bunch
            // warnings with a later writer. Reserve first to suppress a reentrant listener.
            RejectionAborted(
                gate,
                policyName ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty,
                suppressedCount);
            _lastAbortTimestamp = Stopwatch.GetTimestamp();
        }
    }

    [Event(2, Level = EventLevel.Warning, Message = "Request {2} aborted after {0} rate limit policy '{1}' rejected a committed response ({3} suppressed)")]
    private void RejectionAborted(string gate, string policyName, string requestId, int suppressedCount)
        => WriteEvent(2, gate, policyName, requestId, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedRequestsCounter ??= new PollingCounter("rejected-requests", this, () => RejectedRequests)
            {
                DisplayName = "Rejected requests",
            };
        }
    }
}
