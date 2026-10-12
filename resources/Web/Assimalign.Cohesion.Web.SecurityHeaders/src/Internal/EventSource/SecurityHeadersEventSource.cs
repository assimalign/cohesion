using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.SecurityHeaders")]
internal sealed class SecurityHeadersEventSource : EventSource
{
    public static readonly SecurityHeadersEventSource Log = new();

    private readonly Lock _warningGate = new();
    private PollingCounter? _unprotectedResponsesCounter;
    private long _unprotectedResponses;
    private long _lastWarningTimestamp;
    private bool _hasWarningTimestamp;
    private int _suppressedCount;

    private SecurityHeadersEventSource()
    {
    }

    internal long UnprotectedResponses => Interlocked.Read(ref _unprotectedResponses);

    [NonEvent]
    public void HeadersNotApplied(IHttpContext context)
    {
        Interlocked.Increment(ref _unprotectedResponses);

        if (!IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            return;
        }

        lock (_warningGate)
        {
            long timestamp = Environment.TickCount64;
            if (_hasWarningTimestamp && timestamp - _lastWarningTimestamp < 1000)
            {
                if (_suppressedCount < int.MaxValue)
                {
                    _suppressedCount++;
                }
                return;
            }

            int suppressedCount = _suppressedCount;
            _suppressedCount = 0;
            _lastWarningTimestamp = timestamp;
            _hasWarningTimestamp = true;
            // The server now stages policy only in its starting callback. The retired middleware
            // unwind branch could observe an earlier commit; a starting callback cannot.
            HeadersNotApplied(
                context.Response.StatusCode.Value,
                context.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                committedBeforeTurn: false,
                suppressedCount,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Security headers were not applied to response {0} for {1}; committed before turn: {2}; {3} warnings suppressed; request {4}")]
    private void HeadersNotApplied(int statusCode, string routeTemplate, bool committedBeforeTurn, int suppressedCount, string requestId)
        => WriteEvent(1, statusCode, routeTemplate, committedBeforeTurn, suppressedCount, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _unprotectedResponsesCounter ??= new PollingCounter("unprotected-responses", this, () => Interlocked.Read(ref _unprotectedResponses))
            {
                DisplayName = "Unprotected Responses",
            };
        }
    }
}
