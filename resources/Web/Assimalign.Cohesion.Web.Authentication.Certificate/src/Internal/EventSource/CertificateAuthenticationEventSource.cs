using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Authentication.Certificate")]
internal sealed class CertificateAuthenticationEventSource : EventSource
{
    public static readonly CertificateAuthenticationEventSource Log = new();

    private PollingCounter? _rejectedCertificatesCounter;
    private long _rejectedCertificates;

    private CertificateAuthenticationEventSource()
    {
    }

    internal long RejectedCertificates => Interlocked.Read(ref _rejectedCertificates);

    [NonEvent]
    public void CertificateRejected(IHttpContext context, string scheme, string reason)
    {
        Interlocked.Increment(ref _rejectedCertificates);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            CertificateRejected(scheme, reason,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Certificate scheme {0} rejected the request: {1}; request {2}")]
    private void CertificateRejected(string scheme, string reason, string requestId)
        => WriteEvent(1, scheme, reason, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedCertificatesCounter ??= new PollingCounter("rejected-certificates", this, () => RejectedCertificates)
            {
                DisplayName = "Rejected certificate requests",
            };
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rejectedCertificatesCounter?.Dispose();
        }

        base.Dispose(disposing);
    }
}
