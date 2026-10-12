using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.ErrorHandling")]
internal sealed class ErrorHandlingEventSource : EventSource
{
    public static readonly ErrorHandlingEventSource Log = new();

    private PollingCounter? _totalFaultsCounter;
    private IncrementingPollingCounter? _faultRateCounter;
    private PollingCounter? _abortedResponsesCounter;
    private long _totalFaults;
    private long _abortedResponses;

    private ErrorHandlingEventSource()
    {
    }

    internal long TotalFaults => Interlocked.Read(ref _totalFaults);

    internal long AbortedResponses => Interlocked.Read(ref _abortedResponses);

    [NonEvent]
    public void PipelineFault(IHttpContext context, Exception exception, bool responseStarted, bool suppressed)
    {
        // The boundary classified this fault before invoking application callbacks or handlers.
        Interlocked.Increment(ref _totalFaults);

        if (suppressed)
        {
            if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
            {
                ExpectedPipelineFault(exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message, responseStarted, GetRequestId(context));
            }
        }
        else if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            PipelineFault(exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message, responseStarted, GetRequestId(context));
        }
    }

    [NonEvent]
    public void ResponseAborted()
    {
        Interlocked.Increment(ref _abortedResponses);
    }

    // Used as a false exception filter: reporting never catches or replaces the handler's exception.
    [NonEvent]
    public bool ErrorHandlerFailed(IHttpContext context, int handlerIndex, IErrorHandler handler, Exception exception)
    {
        if (CanReportFault(context, exception) && IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ErrorHandlerFailed(handlerIndex, handler.GetType().FullName ?? handler.GetType().Name,
                exception.GetType().FullName ?? exception.GetType().Name, exception.Message, GetRequestId(context));
        }

        return false;
    }

    [NonEvent]
    public void DiagnosticsCallbackFailed(IHttpContext context, string callback, Exception exception)
    {
        if (CanReportFault(context, exception) && IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DiagnosticsCallbackFailed(callback, exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message, GetRequestId(context));
        }
    }

    [NonEvent]
    public void ResponseAbortedByPeer(IHttpContext context, Exception exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ResponseAbortedByPeer(exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message, GetRequestId(context));
        }
    }

    [NonEvent]
    private static bool CanReportFault(IHttpContext context, Exception exception)
        => !(exception is OperationCanceledException && context.RequestCancelled.IsCancellationRequested) &&
            context.Features.Get<IWebClientFaultFeature>()?.StatusCode is null &&
            context.Features.Get<IWebResponseClientFaultFeature>() is not { ResponseAbortedByClient: true };

    [NonEvent]
    private static string GetRequestId(IHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;

    [Event(1, Level = EventLevel.Error, Message = "Pipeline fault for request {3} (response started: {2}): {0}: {1}")]
    private void PipelineFault(string exceptionType, string exceptionMessage, bool responseStarted, string requestId)
        => WriteEvent(1, exceptionType, exceptionMessage, responseStarted, requestId);

    [Event(2, Level = EventLevel.Verbose, Message = "Expected pipeline fault for request {3} (response started: {2}): {0}: {1}")]
    private void ExpectedPipelineFault(string exceptionType, string exceptionMessage, bool responseStarted, string requestId)
        => WriteEvent(2, exceptionType, exceptionMessage, responseStarted, requestId);

    [Event(3, Level = EventLevel.Error, Message = "Error handler {0} ({1}) failed for request {4}: {2}: {3}")]
    private void ErrorHandlerFailed(int handlerIndex, string handlerType, string exceptionType, string exceptionMessage, string requestId)
        => WriteEvent(3, handlerIndex, handlerType, exceptionType, exceptionMessage, requestId);

    [Event(4, Level = EventLevel.Warning, Message = "Diagnostics callback {0} failed for request {3}: {1}: {2}")]
    private void DiagnosticsCallbackFailed(string callback, string exceptionType, string exceptionMessage, string requestId)
        => WriteEvent(4, callback, exceptionType, exceptionMessage, requestId);

    [Event(5, Level = EventLevel.Verbose, Message = "Response abandoned by peer for request {2}: {0}: {1}")]
    private void ResponseAbortedByPeer(string exceptionType, string exceptionMessage, string requestId)
        => WriteEvent(5, exceptionType, exceptionMessage, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        _totalFaultsCounter ??= new PollingCounter("total-faults", this, () => TotalFaults)
        {
            DisplayName = "Total Application Faults",
        };
        _faultRateCounter ??= new IncrementingPollingCounter("faults-per-second", this, () => TotalFaults)
        {
            DisplayName = "Application Fault Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _abortedResponsesCounter ??= new PollingCounter("aborted-responses", this, () => AbortedResponses)
        {
            DisplayName = "Responses Aborted by the Exception Boundary",
        };
    }
}
