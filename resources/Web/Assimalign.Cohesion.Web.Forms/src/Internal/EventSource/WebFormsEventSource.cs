using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Forms")]
internal sealed class WebFormsEventSource : EventSource
{
    public static readonly WebFormsEventSource Log = new();

    private PollingCounter? _formLimitRejectionsCounter;
    private PollingCounter? _malformedFormsCounter;
    private long _formLimitRejections;
    private long _malformedForms;
    private long _lastRejectionAbortedTimestamp;
    private int _suppressedRejections;
    private bool _rejectionAbortedWritten;
    private readonly object _rejectionLock = new();

    private WebFormsEventSource()
    {
    }

    internal long FormLimitRejections => Interlocked.Read(ref _formLimitRejections);

    internal long MalformedForms => Interlocked.Read(ref _malformedForms);

    [NonEvent]
    public void FormLimitExceeded(HttpFormLimitExceededException exception, IHttpContext context)
    {
        Interlocked.Increment(ref _formLimitRejections);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            // The exception's message can be supplied by a custom form feature and echo input.
            FormLimitExceeded(exception.Limit.ToString(), RequestId(context));
        }
    }

    [NonEvent]
    public void FormMalformed(InvalidDataException exception, IHttpContext context)
    {
        Interlocked.Increment(ref _malformedForms);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            FormMalformed(exception.GetType().FullName ?? exception.GetType().Name, RequestId(context));
        }
    }

    [NonEvent]
    public void RejectionAborted(HttpStatusCode status, IHttpContext context)
    {
        if (!IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            return;
        }

        // Serialize the window and its suppression count. Rejections themselves are counted
        // in the catch whether or not this warning is enabled or suppressed.
        lock (_rejectionLock)
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (_rejectionAbortedWritten && timestamp - _lastRejectionAbortedTimestamp < Stopwatch.Frequency)
            {
                if (_suppressedRejections < int.MaxValue)
                {
                    _suppressedRejections++;
                }
                return;
            }

            int suppressedCount = _suppressedRejections;
            _suppressedRejections = 0;
            _lastRejectionAbortedTimestamp = timestamp;
            _rejectionAbortedWritten = true;
            RejectionAborted((int)status, suppressedCount, RequestId(context));
            _lastRejectionAbortedTimestamp = Stopwatch.GetTimestamp();
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Form limit {0} exceeded for request {1}")]
    private void FormLimitExceeded(string limit, string requestId)
        => WriteEvent(1, limit, requestId);

    [Event(2, Level = EventLevel.Verbose, Message = "Malformed form ({0}) for request {1}")]
    private void FormMalformed(string exceptionType, string requestId)
        => WriteEvent(2, exceptionType, requestId);

    [Event(3, Level = EventLevel.Warning, Message = "Form rejection {0} aborted request {2}; {1} earlier warnings suppressed")]
    private void RejectionAborted(int statusCode, int suppressedCount, string requestId)
        => WriteEvent(3, statusCode, suppressedCount, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        _formLimitRejectionsCounter ??= new PollingCounter("form-limit-rejections", this, () => FormLimitRejections)
        {
            DisplayName = "Form Limit Rejections",
        };
        _malformedFormsCounter ??= new PollingCounter("malformed-forms", this, () => MalformedForms)
        {
            DisplayName = "Malformed Forms",
        };
    }

    [NonEvent]
    private static string RequestId(IHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;
}
