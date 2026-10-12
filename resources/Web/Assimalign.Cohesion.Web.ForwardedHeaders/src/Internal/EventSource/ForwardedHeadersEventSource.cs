using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.ForwardedHeaders")]
internal sealed class ForwardedHeadersEventSource : EventSource
{
    public static readonly ForwardedHeadersEventSource Log = new();

    private PollingCounter? _untrustedForwardingCounter;
    private PollingCounter? _malformedForwardingCounter;
    private long _untrustedForwarding;
    private long _malformedForwarding;

    private ForwardedHeadersEventSource()
    {
    }

    internal long UntrustedForwarding => Interlocked.Read(ref _untrustedForwarding);

    internal long MalformedForwarding => Interlocked.Read(ref _malformedForwarding);

    [NonEvent]
    public void ForwardingStopped(
        ForwardingStopReason reason,
        bool forwarded,
        int hopsApplied,
        EndPoint? transportPeer,
        IHttpContext? context)
    {
        if (reason == ForwardingStopReason.UntrustedPeer)
        {
            Interlocked.Increment(ref _untrustedForwarding);
        }
        else if (reason is ForwardingStopReason.MalformedFor or ForwardingStopReason.InvalidProto
            or ForwardingStopReason.ImplausibleHost or ForwardingStopReason.UnusableForwardedHeader)
        {
            Interlocked.Increment(ref _malformedForwarding);
        }

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            // Only a directly connected, untrusted IP peer is diagnostic here. An
            // intermediate address came from a header and must never reach the sink.
            string untrustedPeer = reason == ForwardingStopReason.UntrustedPeer && hopsApplied == 0
                && transportPeer is IPEndPoint ip ? ip.Address.ToString() : string.Empty;
            ForwardingStopped(
                reason.ToString(),
                forwarded ? "Forwarded" : "X-Forwarded",
                hopsApplied,
                untrustedPeer,
                context?.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Forwarding stopped ({0}) in {1} after {2} hops; untrusted peer {3}; request {4}")]
    private void ForwardingStopped(string reason, string family, int hopsApplied, string untrustedPeer, string requestId)
        => WriteEvent(1, reason, family, hopsApplied, untrustedPeer, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _untrustedForwardingCounter ??= new PollingCounter("untrusted-forwarding", this, () => UntrustedForwarding)
            {
                DisplayName = "Forwarding stopped at an untrusted peer",
            };
            _malformedForwardingCounter ??= new PollingCounter("malformed-forwarding", this, () => MalformedForwarding)
            {
                DisplayName = "Forwarding stopped by malformed input",
            };
        }
    }
}
