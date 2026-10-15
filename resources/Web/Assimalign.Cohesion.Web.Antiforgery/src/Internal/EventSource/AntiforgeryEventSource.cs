using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Antiforgery")]
internal sealed class AntiforgeryEventSource : EventSource
{
    public static readonly AntiforgeryEventSource Log = new();

    private const string repositoryReadFailure = "The key repository could not be read to resolve the key that produced this payload.";

    private readonly object _abortThrottleLock = new();
    private PollingCounter? _rejectedRequestsCounter;
    private long _rejectedRequests;
    private long _nextAbortTimestamp;
    private int _suppressedAborts;

    private AntiforgeryEventSource()
    {
    }

    internal long RejectedRequests => Interlocked.Read(ref _rejectedRequests);

    [NonEvent]
    public void RequestRejected(IHttpContext context, string reason, HttpStatusCode status, string tokenLocation)
    {
        // The marker belongs to the exchange; re-entering the middleware must not count it twice.
        if (context.Features.Get<AntiforgeryRejectionFeature>() is not null)
        {
            return;
        }

        context.Features.Set(AntiforgeryRejectionFeature.Instance);
        Interlocked.Increment(ref _rejectedRequests);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            RequestRejected(reason, (int)status, tokenLocation,
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [NonEvent]
    public void RejectionAborted(IHttpContext context, string reason)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && TryReportAbort(out int suppressedCount))
        {
            RejectionAborted(reason,
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                suppressedCount,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [NonEvent]
    public void TokenUnprotectFailed(DataProtectionException exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            // Applications can supply their own protectors and exception text. Only these exact
            // built-in messages are safe; none contains a token, key id, path, or request input.
            string message = exception.Message switch
            {
                "The protected payload is malformed." => "The protected payload is malformed.",
                "The protected payload could not be verified." => "The protected payload could not be verified.",
                "The key that produced this payload is unknown to the ring." => "The key that produced this payload is unknown to the ring.",
                "The key that produced this payload has been revoked." => "The key that produced this payload has been revoked.",
                "The key that produced this payload has aged out of the unprotect grace window." => "The key that produced this payload has aged out of the unprotect grace window.",
                repositoryReadFailure => repositoryReadFailure,
                _ => string.Empty,
            };

            TokenUnprotectFailed(exception.GetType().FullName ?? exception.GetType().Name, message,
                message == repositoryReadFailure && exception.InnerException is { } inner
                    ? inner.GetType().FullName ?? inner.GetType().Name
                    : string.Empty);
        }
    }

    [NonEvent]
    private bool TryReportAbort(out int suppressedCount)
    {
        lock (_abortThrottleLock)
        {
            long now = Environment.TickCount64;
            if (now < _nextAbortTimestamp)
            {
                if (_suppressedAborts < int.MaxValue)
                {
                    _suppressedAborts++;
                }

                suppressedCount = 0;
                return false;
            }

            _nextAbortTimestamp = now + 1000;
            suppressedCount = _suppressedAborts;
            _suppressedAborts = 0;
            return true;
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Antiforgery rejected request {4}: {0}, status {1}, token location {2}, route {3}")]
    private void RequestRejected(string reason, int statusCode, string tokenLocation, string routeTemplate, string requestId)
        => WriteEvent(1, reason, statusCode, tokenLocation, routeTemplate, requestId);

    [Event(2, Level = EventLevel.Warning, Message = "Antiforgery aborted request {3} after the response started: {0}, route {1}; {2} earlier aborts suppressed")]
    private void RejectionAborted(string reason, string routeTemplate, int suppressedCount, string requestId)
        => WriteEvent(2, reason, routeTemplate, suppressedCount, requestId);

    [Event(3, Level = EventLevel.Verbose, Message = "Antiforgery token unprotect failed: {0}: {1}; repository exception {2}")]
    private void TokenUnprotectFailed(string exceptionType, string exceptionMessage, string innerExceptionType)
        => WriteEvent(3, exceptionType, exceptionMessage, innerExceptionType);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedRequestsCounter ??= new PollingCounter("rejected-requests", this, () => RejectedRequests)
            {
                DisplayName = "Rejected antiforgery requests",
            };
        }
    }
}
