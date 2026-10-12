using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Sessions")]
internal sealed class WebSessionEventSource : EventSource
{
    public static readonly WebSessionEventSource Log = new();

    private long _orphanedSessions;
    private PollingCounter? _orphanedSessionsCounter;

    private WebSessionEventSource() { }

    internal long OrphanedSessions => Interlocked.Read(ref _orphanedSessions);

    [NonEvent]
    public void SessionOrphaned(IHttpContext context)
    {
        Interlocked.Increment(ref _orphanedSessions);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            SessionOrphaned("ResponseStarted", context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Session orphaned ({0}) for request {1}")]
    private void SessionOrphaned(string reason, string requestId)
        => WriteEvent(1, reason, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _orphanedSessionsCounter ??= new PollingCounter("orphaned-sessions", this, () => OrphanedSessions)
            {
                DisplayName = "Orphaned sessions",
            };
        }
    }
}
