using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.HostFiltering.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.HostFiltering")]
internal sealed class HostFilteringEventSource : EventSource
{
    public static readonly HostFilteringEventSource Log = new();

    private PollingCounter? _rejectedRequestsCounter;
    private long _rejectedRequests;

    private HostFilteringEventSource()
    {
    }

    internal long RejectedRequests => Interlocked.Read(ref _rejectedRequests);

    [NonEvent]
    public void RequestRejected(IHttpContext context, HttpHost host, bool emptyHost)
    {
        Interlocked.Increment(ref _rejectedRequests);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            string hostName = emptyHost ? string.Empty : host.Host;
            if (hostName.Length > 253)
            {
                hostName = hostName[..253];
            }

            RequestRejected(
                emptyHost ? "EmptyHost" : "HostNotAllowed",
                hostName,
                context.Features.Get<IHttpForwardedFeature>() is { TrustedHopCount: > 0 },
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Host rejected ({0}): {1}; forwarded={2}; request {3}")]
    private void RequestRejected(string reason, string host, bool forwarded, string requestId)
        => WriteEvent(1, reason, host, forwarded, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedRequestsCounter ??= new PollingCounter("rejected-requests", this, () => RejectedRequests)
            {
                DisplayName = "Rejected host requests",
            };
        }
    }
}
