using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Hosting")]
internal sealed class WebHostingEventSource : EventSource
{
    public static readonly WebHostingEventSource Log = new();

    private long _rejectedCredentials;
    private readonly EventThrottle _credentialThrottle = new();
    private readonly EventThrottle _malformedThrottle = new();
    private PollingCounter? _rejectedCredentialsCounter;

    private WebHostingEventSource() { }

    internal long RejectedCredentials => Interlocked.Read(ref _rejectedCredentials);

    [NonEvent]
    public void ControlPlaneDisabled(string? resourceName, IResourceControlPlane controlPlane)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ControlPlaneDisabled(resourceName ?? string.Empty, string.Join(", ", controlPlane.ObservedEndpoints.Keys));
        }
    }

    [NonEvent]
    internal void CredentialRejected(ResourceContext resource, IHttpContext context,
        ResourceCredentialStatus status, CredentialRejectionReason reason)
    {
        Interlocked.Increment(ref _rejectedCredentials);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _credentialThrottle.TryEmit(out int suppressed))
        {
            CredentialRejected(resource.ResourceName ?? string.Empty, GetRequestId(context), status.ToString(),
                ReasonCode(reason), Route(context.Request.Path.Value),
                context.ConnectionInfo.RemoteEndPoint?.ToString() ?? string.Empty, suppressed);
        }
    }

    [NonEvent]
    internal void CommandMalformed(ResourceContext resource, IHttpContext context, string phase, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _malformedThrottle.TryEmit(out int suppressed))
        {
            CommandMalformed(resource.ResourceName ?? string.Empty, GetRequestId(context),
                context.Request.Method == HttpMethod.Delete ? "DELETE" : "POST", phase,
                exception.GetType().FullName ?? exception.GetType().Name, suppressed);
        }
    }

    [NonEvent]
    internal void StopAccepted(ResourceContext resource, IHttpContext context, bool deferred)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            StopAccepted(resource.ResourceName ?? string.Empty, GetRequestId(context), deferred);
        }
    }

    [NonEvent]
    private static string GetRequestId(IHttpContext exchange)
        => exchange.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;

    [NonEvent]
    private static string Route(string path) => path switch
    {
        "/cohesion/v1/stop" => "stop",
        "/cohesion/v1/commands" => "commands",
        "/cohesion/v1/endpoints" => "endpoints",
        "/cohesion/v1/healthz" => "healthz",
        "/cohesion/v1/readyz" => "readyz",
        "/cohesion/v1/livez" => "livez",
        _ => "unknown",
    };

    [NonEvent]
    private static string ReasonCode(CredentialRejectionReason reason) => reason switch
    {
        CredentialRejectionReason.MissingCredential => "missing-credential",
        CredentialRejectionReason.UnsupportedScheme => "unsupported-scheme",
        CredentialRejectionReason.InvalidToken => "invalid-token",
        CredentialRejectionReason.AudienceMismatch => "audience-mismatch",
        CredentialRejectionReason.CallerMismatch => "caller-mismatch",
        CredentialRejectionReason.VerifierRejected => "verifier-rejected",
        CredentialRejectionReason.NonLoopbackPeer => "non-loopback-peer",
        _ => "verifier-rejected",
    };

    [Event(1, Level = EventLevel.Warning, Message = "Resource {0} control plane disabled; observed endpoints: {1}")]
    private void ControlPlaneDisabled(string resourceName, string observedEndpoints)
        => WriteEvent(1, resourceName, observedEndpoints);

    [Event(2, Level = EventLevel.Warning, Message = "Resource {0} rejected a credential ({2}, {3}) on {4}; suppressed {6}")]
    private void CredentialRejected(string resourceName, string requestId, string status, string reason,
        string route, string remoteEndPoint, int suppressedCount)
        => WriteEvent(2, resourceName, requestId, status, reason, route, remoteEndPoint, suppressedCount);

    [Event(3, Level = EventLevel.Warning, Message = "Resource {0} refused malformed {2} command in {3}: {4}; suppressed {5}")]
    private void CommandMalformed(string resourceName, string requestId, string httpMethod, string phase,
        string exceptionType, int suppressedCount)
        => WriteEvent(3, resourceName, requestId, httpMethod, phase, exceptionType, suppressedCount);

    [Event(4, Level = EventLevel.Informational, Message = "Resource {0} accepted stop; deferred {2}")]
    private void StopAccepted(string resourceName, string requestId, bool deferred)
        => WriteEvent(4, resourceName, requestId, deferred);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedCredentialsCounter ??= new PollingCounter("rejected-credentials", this,
                () => RejectedCredentials) { DisplayName = "Rejected Credentials" };
        }
    }
}
