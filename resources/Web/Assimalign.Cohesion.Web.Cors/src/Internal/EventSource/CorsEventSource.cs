using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Cors.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Cors")]
internal sealed class CorsEventSource : EventSource
{
    public static readonly CorsEventSource Log = new();

    private long _rejectedPreflights;
    private PollingCounter? _rejectedPreflightsCounter;

    private CorsEventSource()
    {
    }

    internal long RejectedPreflights => Interlocked.Read(ref _rejectedPreflights);

    [NonEvent]
    public void PreflightRejected(IHttpContext context, CorsPreflightRejection reason, string requestedMethod, ReadOnlySpan<char> rejectedHeader)
    {
        Interlocked.Increment(ref _rejectedPreflights);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            string origin = string.Empty;
            if (context.Request.Headers.TryGetValue(HttpHeaderKey.Origin, out HttpHeaderValue values)
                && values.Count == 1 && values[0] is { } value && CorsOrigin.IsSerialized(value))
            {
                origin = value.Length > 256 ? value[..256] : value;
            }

            PreflightRejected(reason.ToString(), origin,
                requestedMethod.Length > 32 ? requestedMethod[..32] : requestedMethod,
                rejectedHeader[..Math.Min(rejectedHeader.Length, 64)].ToString(),
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "CORS preflight rejected: {0}; origin {1}, method {2}, header {3}, route {4}, request {5}")]
    private void PreflightRejected(string reason, string origin, string requestedMethod, string rejectedHeader, string routeTemplate, string requestId)
        => WriteEvent(1, reason, origin, requestedMethod, rejectedHeader, routeTemplate, requestId);

    [NonEvent]
    public void PreflightPassedThrough(IHttpContext context, string cause, string requestedMethod)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            PreflightPassedThrough(cause, requestedMethod,
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(2, Level = EventLevel.Verbose, Message = "CORS preflight passed through: {0}; method {1}, route {2}, request {3}")]
    private void PreflightPassedThrough(string cause, string requestedMethod, string routeTemplate, string requestId)
        => WriteEvent(2, cause, requestedMethod, routeTemplate, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedPreflightsCounter ??= new PollingCounter("rejected-preflights", this, () => RejectedPreflights)
            {
                DisplayName = "Rejected CORS preflights",
            };
        }
    }
}
