using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.RequestTimeouts")]
internal sealed class RequestTimeoutEventSource : EventSource
{
    public static readonly RequestTimeoutEventSource Log = new();

    private readonly Lock _throttleLock = new();
    private long _timedOutRequests;
    private long _ignoredTimeouts;
    private long _lastWarning;
    private bool _hasWarning;
    private int _suppressedWarnings;
    private PollingCounter? _timedOutCounter;
    private PollingCounter? _ignoredCounter;

    private RequestTimeoutEventSource()
    {
    }

    internal long TimedOutRequests => Interlocked.Read(ref _timedOutRequests);
    internal long IgnoredTimeouts => Interlocked.Read(ref _ignoredTimeouts);

    [NonEvent]
    public void RequestTimedOut(IHttpContext context, TimeSpan timeout, bool responseStarted, int statusCode)
    {
        Interlocked.Increment(ref _timedOutRequests);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            lock (_throttleLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (_hasWarning && Stopwatch.GetElapsedTime(_lastWarning, now) < TimeSpan.FromSeconds(1))
                {
                    if (_suppressedWarnings < int.MaxValue)
                    {
                        _suppressedWarnings++;
                    }

                    return;
                }

                _hasWarning = true;
                _lastWarning = now;
                int suppressedCount = _suppressedWarnings;
                _suppressedWarnings = 0;
                RequestTimedOut(timeout.TotalMilliseconds, responseStarted, statusCode,
                    context.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                    suppressedCount, context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
                _lastWarning = Stopwatch.GetTimestamp();
            }
        }
    }

    [NonEvent]
    public void TimeoutIgnored(IHttpContext context, TimeSpan timeout)
    {
        Interlocked.Increment(ref _ignoredTimeouts);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            TimeoutIgnored(timeout.TotalMilliseconds,
                context.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _timedOutCounter ??= new PollingCounter("timed-out-requests", this, () => TimedOutRequests)
            {
                DisplayName = "Timed-out requests"
            };
            _ignoredCounter ??= new PollingCounter("ignored-timeouts", this, () => IgnoredTimeouts)
            {
                DisplayName = "Ignored request timeouts"
            };
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Request {5} timed out after {0} ms; response started: {1}, status: {2}, route: {3}; suppressed: {4}")]
    private void RequestTimedOut(double timeoutMilliseconds, bool responseStarted, int statusCode, string routeTemplate, int suppressedCount, string requestId)
        => WriteEvent(1, timeoutMilliseconds, responseStarted, statusCode, routeTemplate, suppressedCount, requestId);

    [Event(2, Level = EventLevel.Verbose, Message = "Request {2} completed after ignoring its {0} ms timeout, route: {1}")]
    private void TimeoutIgnored(double timeoutMilliseconds, string routeTemplate, string requestId)
        => WriteEvent(2, timeoutMilliseconds, routeTemplate, requestId);
}
