using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Authorization")]
internal sealed class AuthorizationEventSource : EventSource
{
    public static readonly AuthorizationEventSource Log = new();

    private PollingCounter? _challengedRequestsCounter;
    private PollingCounter? _forbiddenRequestsCounter;
    private long _challengedRequests;
    private long _forbiddenRequests;

    private AuthorizationEventSource()
    {
    }

    internal long ChallengedRequests => Volatile.Read(ref _challengedRequests);

    internal long ForbiddenRequests => Volatile.Read(ref _forbiddenRequests);

    [NonEvent]
    public void RequestDenied(
        IHttpContext context,
        IRouteMatchFeature? endpoint,
        AuthorizationPolicy policy,
        int failedRequirement,
        bool forbid)
    {
        if (forbid)
        {
            Interlocked.Increment(ref _forbiddenRequests);
        }
        else
        {
            Interlocked.Increment(ref _challengedRequests);
        }

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            RequestDenied(
                forbid ? "Forbid" : "Challenge",
                context.Features.Get<IRouterEndpointFeature>()?.RouteTemplate ?? string.Empty,
                endpoint?.Metadata.GetMetadata<AuthorizationMetadata>() is null ? "Fallback" : "Endpoint",
                policy.Requirements[failedRequirement].GetType().Name,
                string.Join(", ", policy.AuthenticationSchemes),
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Authorization {0} for {1} under {2}: {3}; schemes {4}; request {5}")]
    private void RequestDenied(
        string outcome,
        string routeTemplate,
        string policySource,
        string failedRequirement,
        string schemes,
        string requestId)
        => WriteEvent(1, outcome, routeTemplate, policySource, failedRequirement, schemes, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        _challengedRequestsCounter ??= new PollingCounter("challenged-requests", this, () => ChallengedRequests)
        {
            DisplayName = "Challenged Requests",
        };
        _forbiddenRequestsCounter ??= new PollingCounter("forbidden-requests", this, () => ForbiddenRequests)
        {
            DisplayName = "Forbidden Requests",
        };
    }
}
