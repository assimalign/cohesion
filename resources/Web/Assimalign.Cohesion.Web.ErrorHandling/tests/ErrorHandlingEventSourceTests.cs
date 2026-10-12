using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.ErrorHandling.Internal;
using Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>Checks event contracts through the real exception boundary and registered handler chain.</summary>
[Collection(ErrorHandlingEventSourceCollection.Name)]
public sealed class ErrorHandlingEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - EventSource name: Should match its assembly")]
    public void GetName_ErrorHandlingSource_ShouldMatchAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(ErrorHandlingEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.ErrorHandling");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Strict manifest: Should accept all event contracts")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(ErrorHandlingEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Fault event: Should use the request feature and count exactly once")]
    public async Task InvokeAsync_ApplicationFault_ShouldReportOnceWithFeatureRequestId()
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using Activity ambient = new Activity("unrelated-background-work").SetIdFormat(ActivityIdFormat.W3C).Start();
        TestHttpContext context = CreateContext();
        IOException fault = new("Application-owned I/O failed.");
        Exception? observed = null;
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            OnException = (_, exception) =>
            {
                observed = exception;
                return ValueTask.CompletedTask;
            }
        });

        // Act
        await boundary.InvokeAsync(context, _ => throw fault);

        // Assert
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, 1, "PipelineFault", EventLevel.Error,
            "exceptionType", "exceptionMessage", "responseStarted", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[] { typeof(IOException).FullName, fault.Message, false, RequestId(context) });
        RequestId(context).ShouldNotBe(ambient.TraceId.ToHexString());
        EventsFor(recorder, context).Length.ShouldBe(1);
        observed.ShouldBeSameAs(fault);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(1);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Missing request feature: Should not fall back to an unrelated ambient activity")]
    public async Task InvokeAsync_MissingRequestFeature_ShouldUseEmptyRequestId()
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using Activity ambient = new Activity("unrelated-background-work").SetIdFormat(ActivityIdFormat.W3C).Start();
        TestHttpContext context = new();
        string marker = Guid.NewGuid().ToString("N");
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());

        // Act
        await boundary.InvokeAsync(context, _ => throw new InvalidOperationException(marker));

        // Assert
        EventWrittenEventArgs recorded = recorder.Events.Where(value => value.EventId == 1 &&
            value.Payload is { } payload && Equals(payload[1], marker)).ShouldHaveSingleItem();
        recorded.Payload![3].ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(value => value.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Suppression: Should evaluate once and report the expected fault at Verbose")]
    public async Task InvokeAsync_SuppressedFault_ShouldEvaluateOnceAndRaiseExpectedEvent()
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = CreateContext();
        InvalidOperationException fault = new("Expected application failure.");
        int suppressionCalls = 0;
        int observationCalls = 0;
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            SuppressDiagnosticsCallback = (_, exception) =>
            {
                exception.ShouldBeSameAs(fault);
                suppressionCalls++;
                return true;
            },
            OnException = (_, _) =>
            {
                observationCalls++;
                return ValueTask.CompletedTask;
            }
        });

        // Act
        await boundary.InvokeAsync(context, _ => throw fault);

        // Assert
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, 2, "ExpectedPipelineFault", EventLevel.Verbose,
            "exceptionType", "exceptionMessage", "responseStarted", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[] { typeof(InvalidOperationException).FullName, fault.Message, false, RequestId(context) });
        EventsFor(recorder, context).Length.ShouldBe(1);
        suppressionCalls.ShouldBe(1);
        observationCalls.ShouldBe(0);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Handler fault: Should report the handler and preserve the thrown exception")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_HandlerThrows_ShouldReportAndPropagateUnchanged(bool useBoundary, bool asynchronous)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = CreateContext();
        InvalidOperationException handlerFault = new("Registered handler failed.");
        InvalidDataException originalFault = new("Original application failure.");
        RecordingErrorHandler first = new(handles: false);
        RecordingErrorHandler last = new(handles: true);
        IErrorHandlingFeature feature = new ErrorHandlingBuilder()
            .OnError(first)
            .OnError(new EventSourceThrowingErrorHandler(handlerFault, asynchronous))
            .OnError(last)
            .Build();
        context.Features.Set(feature);
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());
        long total = ErrorHandlingEventSource.Log.TotalFaults;

        // Act
        InvalidOperationException caught = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            if (useBoundary)
            {
                await boundary.InvokeAsync(context, _ => throw originalFault);
            }
            else
            {
                await feature.HandleAsync(context, originalFault, CancellationToken.None);
            }
        });

        // Assert
        caught.ShouldBeSameAs(handlerFault);
        first.WasConsulted.ShouldBeTrue();
        last.WasConsulted.ShouldBeFalse();
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, 3, "ErrorHandlerFailed", EventLevel.Error,
            "handlerIndex", "handlerType", "exceptionType", "exceptionMessage", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[]
        {
            1, typeof(EventSourceThrowingErrorHandler).FullName, typeof(InvalidOperationException).FullName,
            handlerFault.Message, RequestId(context)
        });
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(useBoundary ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Handler client outcome: Should preserve the exception without reporting an application fault")]
    [InlineData(false, "Cancelled")]
    [InlineData(false, "RequestClientFault")]
    [InlineData(false, "ResponseClientFault")]
    [InlineData(true, "Cancelled")]
    [InlineData(true, "RequestClientFault")]
    [InlineData(true, "ResponseClientFault")]
    public async Task HandleAsync_HandlerObservesClientOutcome_ShouldExcludeEventsAndCounters(bool useBoundary, string outcome)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource request = new();
        TestHttpContext context = CreateContext();
        context.RequestCancelled = request.Token;
        InvalidOperationException handlerFault = new("Handler wrapped a client-caused failure.");
        InvalidDataException originalFault = new("Original application failure.");
        IErrorHandlingFeature feature = new ErrorHandlingBuilder().OnError((_, _, _) =>
        {
            if (outcome == "Cancelled")
            {
                request.Cancel();
            }
            else if (outcome == "RequestClientFault")
            {
                context.Features.Set<IWebClientFaultFeature>(new FakeClientFaultFeature(HttpStatusCode.RequestEntityTooLarge));
            }
            else
            {
                context.Features.Set<IWebResponseClientFaultFeature>(new EventSourceResponseClientFaultFeature { ResponseAbortedByClient = true });
            }

            throw handlerFault;
        }).Build();
        context.Features.Set(feature);
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;

        // Act
        InvalidOperationException caught = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            if (useBoundary)
            {
                await boundary.InvokeAsync(context, _ => throw originalFault);
            }
            else
            {
                await feature.HandleAsync(context, originalFault, CancellationToken.None);
            }
        });

        // Assert
        caught.ShouldBeSameAs(handlerFault);
        RequireNoEvents(recorder, context);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(0);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Handler unwind cancellation: Should classify after the handler finally runs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_HandlerCancelsInFinally_ShouldPropagateWithoutReportingFault(bool useBoundary)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource request = new();
        TestHttpContext context = CreateContext();
        context.RequestCancelled = request.Token;
        InvalidOperationException handlerFault = new("Handler fault before cancellation during unwind.");
        InvalidDataException originalFault = new("Original application failure.");
        IErrorHandlingFeature feature = new ErrorHandlingBuilder().OnError((_, _, _) =>
        {
            try
            {
                throw handlerFault;
            }
            finally
            {
                request.Cancel();
            }
        }).Build();
        context.Features.Set(feature);
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;

        // Act
        InvalidOperationException caught = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            if (useBoundary)
            {
                await boundary.InvokeAsync(context, _ => throw originalFault);
            }
            else
            {
                await feature.HandleAsync(context, originalFault, CancellationToken.None);
            }
        });

        // Assert
        caught.ShouldBeSameAs(handlerFault);
        request.IsCancellationRequested.ShouldBeTrue();
        RequireNoEvents(recorder, context);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(0);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Diagnostic callback fault: Should report once and keep handling the original fault")]
    [InlineData("OnException")]
    [InlineData("SuppressDiagnosticsCallback")]
    public async Task InvokeAsync_DiagnosticCallbackThrows_ShouldReportAndKeepRendering(string callback)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = CreateContext();
        InvalidOperationException callbackFault = new("Diagnostic callback failed.");
        InvalidDataException originalFault = new("Original application failure.");
        int suppressionCalls = 0;
        int observationCalls = 0;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            SuppressDiagnosticsCallback = (_, _) =>
            {
                suppressionCalls++;
                return callback == "SuppressDiagnosticsCallback" ? throw callbackFault : false;
            },
            OnException = (_, _) =>
            {
                observationCalls++;
                return callback == "OnException" ? throw callbackFault : ValueTask.CompletedTask;
            }
        });
        long total = ErrorHandlingEventSource.Log.TotalFaults;

        // Act
        await boundary.InvokeAsync(context, _ => throw originalFault);

        // Assert
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, 4, "DiagnosticsCallbackFailed", EventLevel.Warning,
            "callback", "exceptionType", "exceptionMessage", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[]
        {
            callback, typeof(InvalidOperationException).FullName, callbackFault.Message, RequestId(context)
        });
        EventsFor(recorder, context).Count(value => value.EventId == 1).ShouldBe(1);
        suppressionCalls.ShouldBe(1);
        observationCalls.ShouldBe(1);
        context.Features.Get<IHttpExceptionFeature>()!.Error.ShouldBeSameAs(originalFault);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Peer abort: Should follow the latch and exclude both fault counters")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ResponseClientFault_ShouldRaiseOnlyPeerEvent(bool responseStarted)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource request = new();
        await request.CancelAsync();
        TestHttpContext context = CreateContext();
        context.RequestCancelled = request.Token;
        context.Features.Set<IWebResponseClientFaultFeature>(new EventSourceResponseClientFaultFeature { ResponseAbortedByClient = true });
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(responseStarted));
        InvalidOperationException wrapped = new("Adapter wrapped the abandoned response.");
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;
        int observations = 0;
        int suppressions = 0;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            SuppressDiagnosticsCallback = (_, _) => { suppressions++; return false; },
            OnException = (_, _) => { observations++; return ValueTask.CompletedTask; }
        });

        // Act — direct catch preserves the actual cancellation exception and its inner exception.
        OperationCanceledException? caught = null;
        try
        {
            await boundary.InvokeAsync(context, _ => throw wrapped);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        // Assert
        caught.ShouldNotBeNull().CancellationToken.ShouldBe(request.Token);
        caught.InnerException.ShouldBeSameAs(wrapped);
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, 5, "ResponseAbortedByPeer", EventLevel.Verbose,
            "exceptionType", "exceptionMessage", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[] { typeof(InvalidOperationException).FullName, wrapped.Message, RequestId(context) });
        EventsFor(recorder, context).Length.ShouldBe(1);
        observations.ShouldBe(0);
        suppressions.ShouldBe(0);
        context.CancelRequested.ShouldBeTrue();
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(0);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Request client fault: Should emit no application fault and count neither total")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_RequestClientFault_ShouldExcludeEventsAndCounters(bool responseStarted)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = CreateContext();
        context.Features.Set<IWebClientFaultFeature>(new FakeClientFaultFeature(HttpStatusCode.RequestEntityTooLarge));
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(responseStarted));
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());

        // Act
        await boundary.InvokeAsync(context, _ => throw new InvalidOperationException("Wrapped request body fault."));

        // Assert
        RequireNoEvents(recorder, context);
        context.CancelRequested.ShouldBe(responseStarted);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(0);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Request cancellation: Should emit no application fault even for a wrapped exception")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_RequestCancelled_ShouldExcludeEventsAndCounters(bool wrapped)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource request = new();
        await request.CancelAsync();
        TestHttpContext context = CreateContext();
        context.RequestCancelled = request.Token;
        Exception fault = wrapped ? new InvalidOperationException("Wrapped cancellation.") : new OperationCanceledException(request.Token);
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions());

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => boundary.InvokeAsync(context, _ => throw fault));

        // Assert
        RequireNoEvents(recorder, context);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(0);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Started response: Should report the application fault and count the boundary abort separately")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_StartedResponse_ShouldReportFaultAndCountBoundaryAbort(bool suppressed)
    {
        // Arrange
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = CreateContext();
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        InvalidOperationException fault = new("Application failed after starting its response.");
        Exception? observed = null;
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            SuppressDiagnosticsCallback = (_, _) => suppressed,
            OnException = (_, exception) => { observed = exception; return ValueTask.CompletedTask; }
        });

        // Act
        await boundary.InvokeAsync(context, _ => throw fault);

        // Assert
        EventWrittenEventArgs recorded = RequireEvent(recorder, context, suppressed ? 2 : 1,
            suppressed ? "ExpectedPipelineFault" : "PipelineFault", suppressed ? EventLevel.Verbose : EventLevel.Error,
            "exceptionType", "exceptionMessage", "responseStarted", "requestId");
        recorded.Payload!.ToArray().ShouldBe(new object?[] { typeof(InvalidOperationException).FullName, fault.Message, true, RequestId(context) });
        EventsFor(recorder, context).Length.ShouldBe(1);
        context.CancelRequested.ShouldBeTrue();
        observed.ShouldBeSameAs(suppressed ? null : fault);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(1);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Disabled source: Should maintain exact counters without reading event payload properties")]
    public async Task InvokeAsync_NoListener_ShouldAvoidPayloadReadsAndMaintainCounters()
    {
        // Arrange
        ErrorHandlingEventSource.Log.IsEnabled().ShouldBeFalse();
        TestHttpContext context = new();
        EventSourceRequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        EventSourceMessageProbeException fault = new();
        EventSourceMessageProbeException callbackFault = new();
        EventSourceMessageProbeException handlerFault = new();
        ExceptionBoundaryMiddleware boundary = new(new ExceptionBoundaryOptions
        {
            SuppressDiagnosticsCallback = (_, _) => throw callbackFault,
            OnException = (_, _) => throw callbackFault
        });
        IErrorHandlingFeature handlers = new ErrorHandlingBuilder()
            .OnError(new EventSourceThrowingErrorHandler(handlerFault, asynchronous: false)).Build();
        long total = ErrorHandlingEventSource.Log.TotalFaults;
        long aborted = ErrorHandlingEventSource.Log.AbortedResponses;

        // Act
        await boundary.InvokeAsync(context, _ => throw fault);
        await Should.ThrowAsync<EventSourceMessageProbeException>(async () =>
            await handlers.HandleAsync(context, fault, CancellationToken.None));
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(true));
        await boundary.InvokeAsync(context, _ => throw fault);

        // Assert
        requestId.ReadCount.ShouldBe(0);
        fault.MessageReadCount.ShouldBe(0);
        callbackFault.MessageReadCount.ShouldBe(0);
        handlerFault.MessageReadCount.ShouldBe(0);
        (ErrorHandlingEventSource.Log.TotalFaults - total).ShouldBe(2);
        (ErrorHandlingEventSource.Log.AbortedResponses - aborted).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Counter publication: Should publish all three declared counters")]
    public async Task OnEventCommand_CountersEnabled_ShouldPublishEveryDeclaredCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(ErrorHandlingEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act
        await recorder.WaitForCountersAsync(new[] { "total-faults", "faults-per-second", "aborted-responses" }, timeout.Token);

        // Assert
        recorder.Events.ShouldNotContain(value => value.EventId == 0);
    }

    private static TestHttpContext CreateContext()
    {
        TestHttpContext context = new();
        context.Features.Set<IWebRequestIdFeature>(new EventSourceRequestIdFeature());
        return context;
    }

    private static string RequestId(TestHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();

    private static EventWrittenEventArgs[] EventsFor(EventSourceRecorder recorder, TestHttpContext context)
    {
        string requestId = RequestId(context);
        return recorder.Events.Where(value => value.PayloadNames is { } names && value.Payload is { } payload &&
            names.IndexOf("requestId") is int index && index >= 0 && Equals(payload[index], requestId)).ToArray();
    }

    private static EventWrittenEventArgs RequireEvent(EventSourceRecorder recorder, TestHttpContext context,
        int id, string name, EventLevel level, params string[] payloadNames)
    {
        recorder.Events.ShouldNotContain(value => value.EventId == 0);
        EventWrittenEventArgs[] matching = EventsFor(recorder, context).Where(value => value.EventId == id).ToArray();
        matching.Length.ShouldBe(1);
        EventWrittenEventArgs recorded = matching[0];
        recorded.EventName.ShouldBe(name);
        recorded.Level.ShouldBe(level);
        recorded.PayloadNames!.ToArray().ShouldBe(payloadNames);
        return recorded;
    }

    private static void RequireNoEvents(EventSourceRecorder recorder, TestHttpContext context)
    {
        recorder.Events.ShouldNotContain(value => value.EventId == 0);
        EventsFor(recorder, context).ShouldBeEmpty();
    }
}
