using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Compression.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Compression")]
internal sealed class CompressionEventSource : EventSource
{
    public static readonly CompressionEventSource Log = new();

    private readonly object _warningLock = new();
    private PollingCounter? _decompressionLimitRejectionsCounter;
    private long _decompressionLimitRejections;
    private long _lastLimitWarning;
    private long _lastAbortWarning;
    private int _suppressedLimitWarnings;
    private int _suppressedAbortWarnings;

    private CompressionEventSource()
    {
    }

    internal long DecompressionLimitRejections => Interlocked.Read(ref _decompressionLimitRejections);

    [NonEvent]
    public void DecompressionRejected(IHttpContext context, string reason, IReadOnlyList<string> codings, Exception? exception = null)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            DecompressionRejected(reason, FormatCodings(codings), exception?.GetType().FullName ?? string.Empty, RequestId(context));
        }
    }

    [NonEvent]
    public void DecompressionLimitExceeded(IHttpContext context, long limitBytes, IReadOnlyList<string> codings)
    {
        // The middleware catch calls this once, even when a handler retried a failed body read.
        Interlocked.Increment(ref _decompressionLimitRejections);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            lock (_warningLock)
            {
                if (TryAcquireWarning(ref _lastLimitWarning, ref _suppressedLimitWarnings, out int suppressedCount))
                {
                    DecompressionLimitExceeded(limitBytes, FormatCodings(codings), suppressedCount, RequestId(context));
                    _lastLimitWarning = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    [NonEvent]
    public void RejectionAborted(IHttpContext context, HttpStatusCode statusCode)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            lock (_warningLock)
            {
                if (TryAcquireWarning(ref _lastAbortWarning, ref _suppressedAbortWarnings, out int suppressedCount))
                {
                    RejectionAborted((int)statusCode, suppressedCount, RequestId(context));
                    _lastAbortWarning = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Request {3} decompression rejected: {0}; codings {1}; exception {2}")]
    private void DecompressionRejected(string reason, string codings, string exceptionType, string requestId)
        => WriteEvent(1, reason, codings, exceptionType, requestId);

    [Event(2, Level = EventLevel.Warning, Message = "Request {3} exceeded the decompression limit of {0} bytes; codings {1}; {2} earlier warnings suppressed")]
    private void DecompressionLimitExceeded(long limitBytes, string codings, int suppressedCount, string requestId)
        => WriteEvent(2, limitBytes, codings, suppressedCount, requestId);

    [Event(3, Level = EventLevel.Warning, Message = "Request {2} aborted because its response started before decompression rejection {0}; {1} earlier warnings suppressed")]
    private void RejectionAborted(int statusCode, int suppressedCount, string requestId)
        => WriteEvent(3, statusCode, suppressedCount, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _decompressionLimitRejectionsCounter ??= new PollingCounter("decompression-limit-rejections", this, () => DecompressionLimitRejections)
            {
                DisplayName = "Decompression Limit Rejections",
            };
        }
    }

    [NonEvent]
    private static bool TryAcquireWarning(ref long lastWarning, ref int suppressedWarnings, out int suppressedCount)
    {
        // The caller holds the lock through the event write, then timestamps its completion so a
        // preempted writer cannot resume beside the next window's event.
        long timestamp = Stopwatch.GetTimestamp();
        if (lastWarning != 0 && Stopwatch.GetElapsedTime(lastWarning, timestamp) < TimeSpan.FromSeconds(1))
        {
            if (suppressedWarnings < int.MaxValue)
            {
                suppressedWarnings++;
            }

            suppressedCount = 0;
            return false;
        }

        lastWarning = timestamp;
        suppressedCount = suppressedWarnings;
        suppressedWarnings = 0;
        return true;
    }

    [NonEvent]
    private static string RequestId(IHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;

    [NonEvent]
    private static string FormatCodings(IReadOnlyList<string> codings)
    {
        // Only fixed recognized tokens reach the payload. A set keeps repeated coding chains bounded.
        int recognized = 0;
        foreach (string coding in codings)
        {
            recognized |= coding switch
            {
                ContentCodings.Gzip => 1,
                ContentCodings.Brotli => 2,
                ContentCodings.Deflate => 4,
                _ => 0,
            };
        }

        return recognized switch
        {
            1 => "gzip",
            2 => "br",
            3 => "gzip,br",
            4 => "deflate",
            5 => "gzip,deflate",
            6 => "br,deflate",
            7 => "gzip,br,deflate",
            _ => string.Empty,
        };
    }
}
