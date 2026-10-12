using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

internal sealed class EventSourceRequestIdFeature : IWebRequestIdFeature
{
    private readonly ActivityTraceId _requestId = ActivityTraceId.CreateRandom();

    public string Name => nameof(IWebRequestIdFeature);
    public int ReadCount { get; private set; }
    public ActivityTraceId RequestId
    {
        get
        {
            ReadCount++;
            return _requestId;
        }
    }
}

internal sealed class EventSourceResponseClientFaultFeature : IWebResponseClientFaultFeature
{
    public string Name => nameof(IWebResponseClientFaultFeature);
    public bool ResponseAbortedByClient { get; set; }
}

internal sealed class EventSourceThrowingErrorHandler : IErrorHandler
{
    private readonly Exception _exception;
    private readonly bool _asynchronous;

    public EventSourceThrowingErrorHandler(Exception exception, bool asynchronous)
    {
        _exception = exception;
        _asynchronous = asynchronous;
    }

    public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _asynchronous ? FailAsync() : throw _exception;
    }

    private async ValueTask<bool> FailAsync()
    {
        await Task.Yield();
        throw _exception;
    }
}

internal sealed class EventSourceMessageProbeException : Exception
{
    public int MessageReadCount { get; private set; }

    public override string Message
    {
        get
        {
            MessageReadCount++;
            return "Diagnostic message probe.";
        }
    }
}
