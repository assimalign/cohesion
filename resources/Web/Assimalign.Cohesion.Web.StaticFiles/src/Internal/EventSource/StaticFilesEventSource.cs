using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.StaticFiles")]
internal sealed class StaticFilesEventSource : EventSource
{
    public static readonly StaticFilesEventSource Log = new();

    private long _traversalRejections;
    private long _mountLookupFailures;
    private long _nextMountLookupReport;
    private int _suppressedMountLookups;
    private PollingCounter? _traversalRejectionsCounter;
    private PollingCounter? _mountLookupFailuresCounter;

    private StaticFilesEventSource() { }

    internal long TraversalRejections => Interlocked.Read(ref _traversalRejections);
    internal long MountLookupFailures => Interlocked.Read(ref _mountLookupFailures);

    [NonEvent]
    public void WebRootMounted(FileSystemPath webRoot, HttpPath prefix)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WebRootMounted(webRoot.ToString(), prefix.Value == "/" ? string.Empty : prefix.Value.TrimEnd('/'));
        }
    }

    [NonEvent]
    public void WebRootUnavailable(FileSystemPath? webRoot, string consumer)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            WebRootUnavailable(webRoot?.ToString() ?? string.Empty, consumer);
        }
    }

    [NonEvent]
    public void TraversalRejected(IHttpContext context, string reason, string prefix)
    {
        Interlocked.Increment(ref _traversalRejections);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            TraversalRejected(reason, prefix, context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [NonEvent]
    public void MountLookupFailed(string prefix, Exception exception)
    {
        Interlocked.Increment(ref _mountLookupFailures);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && TryReportMountLookup(out int suppressedCount))
        {
            // Mount exception messages can contain the client's path.
            MountLookupFailed(prefix, exception.GetType().FullName ?? exception.GetType().Name, suppressedCount);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Web root {0} mounted at {1}")]
    private void WebRootMounted(string webRoot, string prefix) => WriteEvent(1, webRoot, prefix);

    [Event(2, Level = EventLevel.Warning, Message = "Web root {0} unavailable for {1}")]
    private void WebRootUnavailable(string webRoot, string consumer) => WriteEvent(2, webRoot, consumer);

    [Event(3, Level = EventLevel.Verbose, Message = "Static file traversal rejected ({0}) at {1}; request {2}")]
    private void TraversalRejected(string reason, string prefix, string requestId) => WriteEvent(3, reason, prefix, requestId);

    [Event(4, Level = EventLevel.Warning, Message = "Static file mount lookup failed at {0} ({1}); {2} earlier failures suppressed")]
    private void MountLookupFailed(string prefix, string exceptionType, int suppressedCount) => WriteEvent(4, prefix, exceptionType, suppressedCount);

    [NonEvent]
    private bool TryReportMountLookup(out int suppressedCount)
    {
        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref _nextMountLookupReport);
        if (now < next || Interlocked.CompareExchange(ref _nextMountLookupReport, now + Stopwatch.Frequency, next) != next)
        {
            Interlocked.Increment(ref _suppressedMountLookups);
            suppressedCount = 0;
            return false;
        }

        suppressedCount = Interlocked.Exchange(ref _suppressedMountLookups, 0);
        return true;
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _traversalRejectionsCounter ??= new PollingCounter("traversal-rejections", this, () => TraversalRejections)
            {
                DisplayName = "Static file traversal rejections",
            };
            _mountLookupFailuresCounter ??= new PollingCounter("mount-lookup-failures", this, () => MountLookupFailures)
            {
                DisplayName = "Static file mount lookup failures",
            };
        }
    }
}
