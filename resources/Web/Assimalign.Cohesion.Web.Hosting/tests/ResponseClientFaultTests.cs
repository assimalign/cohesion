using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>Client-caused response failures remain cancellation when a writer wraps the exception.</summary>
[Collection(nameof(TelemetryCollection))]
public class ResponseClientFaultTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A streamed callback failure remains an application fault after cancelling its exchange")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task Boundary_ThrowingStreamedStartingCallback_ShouldReportOriginalApplicationFault(int protocol, bool asynchronous)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellation.Token;
        using EventSourceRecorder events = await CreateErrorHandlingRecorderAsync(cancellationToken);
        EventSourceRecorder.CounterSample totalBefore = await events.WaitForCounterAsync("total-faults", 0, cancellationToken);
        EventSourceRecorder.CounterSample abortedBefore = await events.WaitForCounterAsync("aborted-responses", 0, cancellationToken);
        InvalidOperationException callbackFailure = new("Deliberate streamed response-starting failure.");
        TaskCompletionSource<(Exception Exception, bool Cancelled, bool PeerAbort, bool Started)> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource boundaryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = $"/response-starting-fault/{Guid.NewGuid():N}";
        string requestId = string.Empty;
        int callbackCalls = 0;

        await using ResponseBackpressureTransport transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddErrorHandling(errors =>
        {
            errors.OnError((_, _, _) => ValueTask.FromResult(true));
            errors.Options.OnException = (context, exception) =>
            {
                observed.TrySetResult((exception, context.RequestCancelled.IsCancellationRequested,
                    context.Features.Get<IWebResponseClientFaultFeature>()?.ResponseAbortedByClient == true,
                    context.Response.Streaming.HasStarted));
                return ValueTask.CompletedTask;
            };
        });
        factory.Builder.Server.UseServer(options =>
        {
            options.Interceptors.Add(new HttpResponseStreamingInterceptor());
            if (protocol == 1)
            {
                options.UseHttp1(transport.StreamListener);
            }
            else if (protocol == 2)
            {
                options.UseHttp2(transport.StreamListener);
            }
            else
            {
                options.UseHttp3(transport.MultiplexedListener);
            }
        });
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            finally
            {
                boundaryCompleted.TrySetResult();
            }
        });
        factory.Application.UseErrorHandling();
        factory.Application.Use(async (context, _) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()!;
            if (asynchronous)
            {
                starting.OnStarting(async _ =>
                {
                    await Task.Yield();
                    Interlocked.Increment(ref callbackCalls);
                    throw callbackFailure;
                }, null);
            }
            else
            {
                starting.OnStarting(_ =>
                {
                    Interlocked.Increment(ref callbackCalls);
                    throw callbackFailure;
                }, null);
            }

            await context.Response.Streaming.StartAsync(cancellationToken);
        });
        await factory.StartAsync(cancellationToken);
        await using IDuplexConnection? http1 = protocol == 1
            ? await transport.Connections.CreateFactory().ConnectAsync(transport.Connections.EndPoint, cancellationToken)
            : null;
        await using Http2RawClient? http2 = protocol == 2
            ? await Http2RawClient.ConnectAsync(transport.Connections, cancellationToken)
            : null;
        await using Http3RawClient? http3 = protocol == 3
            ? await Http3RawClient.ConnectAsync(transport.MultiplexedConnections, cancellationToken)
            : null;

        // Act — the real response-starting feature cancels before rethrowing the callback exception.
        await using IDuplexConnection? requestStream = http3 is not null
            ? await http3.SendRequestAsync("GET", path, [], [], endStream: true, cancellationToken)
            : null;
        if (http1 is not null)
        {
            Stream stream = http1.AsStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        else if (http2 is not null)
        {
            await http2.SendGetAsync(1, path, cancellationToken);
        }

        var fault = await observed.Task.WaitAsync(cancellationToken);
        await boundaryCompleted.Task.WaitAsync(cancellationToken);
        await factory.StopAsync(cancellationToken);
        long completedSequence = events.CounterSequence;
        EventSourceRecorder.CounterSample totalAfter = await events.WaitForCounterAsync("total-faults", completedSequence, cancellationToken);
        EventSourceRecorder.CounterSample abortedAfter = await events.WaitForCounterAsync("aborted-responses", completedSequence, cancellationToken);

        // Assert — cancellation caused by an application callback does not turn it into a peer fault.
        Volatile.Read(ref callbackCalls).ShouldBe(1);
        fault.Exception.ShouldBeSameAs(callbackFailure);
        fault.Cancelled.ShouldBeTrue();
        fault.PeerAbort.ShouldBeFalse();
        fault.Started.ShouldBeFalse();
        EventWrittenEventArgs[] requestEvents = events.Events.Where(entry =>
            entry.PayloadNames?.IndexOf("requestId") is int index && index >= 0 &&
            Equals(entry.Payload![index], requestId)).ToArray();
        EventWrittenEventArgs applicationFault = requestEvents.ShouldHaveSingleItem();
        applicationFault.EventId.ShouldBe(1);
        applicationFault.EventName.ShouldBe("PipelineFault");
        applicationFault.Level.ShouldBe(EventLevel.Error);
        applicationFault.PayloadNames!.ShouldBe(["exceptionType", "exceptionMessage", "responseStarted", "requestId"]);
        applicationFault.Payload!.ToArray().ShouldBe(new object?[]
        {
            typeof(InvalidOperationException).FullName, callbackFailure.Message, false, requestId,
        });
        totalAfter.Value.ShouldBe(totalBefore.Value + 1);
        abortedAfter.Value.ShouldBe(abortedBefore.Value);
        events.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Response client fault: A slow reader or peer reset should report Verbose only and leave fault counters unchanged")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task Boundary_ResponseAbandonedByClient_ShouldCancelWithoutApplicationFaults(int protocol, bool peerReset)
    {
        // Arrange — a bodyless GET must carry the response-side report independently of request faults.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellation.Token;
        using EventSourceRecorder events = await CreateErrorHandlingRecorderAsync(cancellationToken);
        EventSourceRecorder.CounterSample totalBefore = await events.WaitForCounterAsync("total-faults", 0, cancellationToken);
        EventSourceRecorder.CounterSample abortedBefore = await events.WaitForCounterAsync("aborted-responses", 0, cancellationToken);
        using TelemetryRecorder recorder = new(metrics: false);
        RecordingLoggerProvider recorded = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writeAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource peerCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Exception?> boundaryResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(InvalidOperationException Wrapped, CancellationToken Token, bool? Latched)> writeFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = $"/response-client-fault/{Guid.NewGuid():N}";
        string requestId = string.Empty;
        int exceptionCallbacks = 0;
        int errorHandlers = 0;
        int completionCallbacks = 0;
        HttpMinDataRate rate = new(bytesPerSecond: 1_000_000, gracePeriod: TimeSpan.FromMilliseconds(100));

        await using ResponseBackpressureTransport transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Logging.AddProvider(recorded);
        factory.Builder.Services.AddErrorHandling(errors =>
        {
            errors.OnError((_, _, _) =>
            {
                Interlocked.Increment(ref errorHandlers);
                return ValueTask.FromResult(true);
            });
            errors.Options.OnException = (_, _) =>
            {
                Interlocked.Increment(ref exceptionCallbacks);
                return ValueTask.CompletedTask;
            };
        });
        factory.Builder.Server.UseServer(options =>
        {
            options.Interceptors.Add(new HttpResponseStreamingInterceptor());
            if (protocol == 1)
            {
                options.UseHttp1(transport.StreamListener, http => http.Limits.MinResponseDataRate = rate);
            }
            else if (protocol == 2)
            {
                options.UseHttp2(transport.StreamListener, http => http.Limits.MinResponseDataRate = rate);
            }
            else
            {
                options.UseHttp3(transport.MultiplexedListener, http => http.Limits.MinResponseDataRate = rate);
            }
        });
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next(context);
                boundaryResult.TrySetResult(null);
            }
            // Test observation boundary records whichever exception the middleware emits.
            catch (Exception exception)
            {
                boundaryResult.TrySetResult(exception);
                throw;
            }
        });
        factory.Application.UseErrorHandling();
        factory.Application.Use(async (context, _) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            using CancellationTokenRegistration cancellationRegistration = context.RequestCancelled.Register(
                () => peerCancelled.TrySetResult());
            context.Features.Get<IWebResponseCompletionFeature>()!.Register(() =>
            {
                Interlocked.Increment(ref completionCallbacks);
                return ValueTask.CompletedTask;
            });
            await context.Response.Streaming.WriteAsync("first"u8.ToArray(), cancellationToken);
            await context.Response.Streaming.FlushAsync(cancellationToken);
            started.TrySetResult();
            await writeAgain.Task.WaitAsync(cancellationToken);

            try
            {
                await context.Response.Streaming.WriteAsync(new byte[1024], CancellationToken.None);
                await context.Response.Streaming.FlushAsync(CancellationToken.None);
                // HTTP/2 preserves its silent write return after RST_STREAM. A cooperative content
                // adapter checks the exchange token too and wraps that cancellation in the same way.
                context.RequestCancelled.ThrowIfCancellationRequested();
            }
            // A content adapter may wrap any transport exception; classification must use the latch.
            catch (Exception exception)
            {
                InvalidOperationException wrapped = new("The content adapter wrapped its failed response write.", exception);
                writeFailed.TrySetResult((wrapped, context.RequestCancelled,
                    context.Features.Get<IWebResponseClientFaultFeature>()?.ResponseAbortedByClient));
                throw wrapped;
            }
        });
        await factory.StartAsync(cancellationToken);
        await using IDuplexConnection? http1 = protocol == 1
            ? await transport.Connections.CreateFactory().ConnectAsync(transport.Connections.EndPoint, cancellationToken)
            : null;
        await using Http2RawClient? http2 = protocol == 2
            ? await Http2RawClient.ConnectAsync(transport.Connections, cancellationToken)
            : null;
        await using Http3RawClient? http3 = protocol == 3
            ? await Http3RawClient.ConnectAsync(transport.MultiplexedConnections, cancellationToken)
            : null;
        await using IMultiplexedConnectionStream? requestStream = http3 is not null
            ? await http3.SendRequestAsync("GET", path, [], [], endStream: true, cancellationToken)
            : null;

        try
        {
            if (http1 is not null)
            {
                Stream stream = http1.AsStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            else if (http2 is not null)
            {
                await http2.SendGetAsync(1, path, cancellationToken);
            }
            await started.Task.WaitAsync(cancellationToken);

            // Act — after one successful streamed chunk, stop receiving or abandon the response.
            if (!peerReset)
            {
                transport.StallResponse();
            }
            else if (http1 is not null)
            {
                http1.Abort();
            }
            else if (http2 is not null)
            {
                await http2.SendResetAsync(1, errorCode: 0x8, cancellationToken);
                await peerCancelled.Task.WaitAsync(cancellationToken);
            }
            else
            {
                requestStream!.DirectionalAbort!.AbortRead(0x10c);
            }
            writeAgain.TrySetResult();

            var failure = await writeFailed.Task.WaitAsync(cancellationToken);
            Exception? result = await boundaryResult.Task.WaitAsync(cancellationToken);
            Activity span = await recorder.WaitForStoppedAsync(activity => Equals(activity.GetTagItem("url.path"), path), cancellationToken);
            await factory.StopAsync(cancellationToken);
            long completedSequence = events.CounterSequence;
            EventSourceRecorder.CounterSample totalAfter = await events.WaitForCounterAsync("total-faults", completedSequence, cancellationToken);
            EventSourceRecorder.CounterSample abortedAfter = await events.WaitForCounterAsync("aborted-responses", completedSequence, cancellationToken);

            // Assert — the boundary reads the report, not InvalidOperationException's type or its inner exception.
            failure.Latched.ShouldBe(true);
            failure.Token.IsCancellationRequested.ShouldBeTrue();
            OperationCanceledException canceled = result.ShouldBeAssignableTo<OperationCanceledException>()!;
            canceled.CancellationToken.ShouldBe(failure.Token);
            canceled.InnerException.ShouldBeSameAs(failure.Wrapped);
            Volatile.Read(ref exceptionCallbacks).ShouldBe(0);
            Volatile.Read(ref errorHandlers).ShouldBe(0);
            Volatile.Read(ref completionCallbacks).ShouldBe(0);
            span.GetTagItem("error.type").ShouldBe("request_canceled");
            span.GetTagItem("http.response.status_code").ShouldBe(200);
            recorded.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
            EventWrittenEventArgs[] requestEvents = events.Events.Where(entry =>
                entry.PayloadNames?.IndexOf("requestId") is int index && index >= 0 &&
                Equals(entry.Payload![index], requestId)).ToArray();
            requestEvents.ShouldNotContain(entry => entry.Level == EventLevel.Error);
            EventWrittenEventArgs peerEvent = requestEvents.ShouldHaveSingleItem();
            peerEvent.EventId.ShouldBe(5);
            peerEvent.EventName.ShouldBe("ResponseAbortedByPeer");
            peerEvent.Level.ShouldBe(EventLevel.Verbose);
            peerEvent.PayloadNames!.ShouldBe(["exceptionType", "exceptionMessage", "requestId"]);
            peerEvent.Payload![0].ShouldBe(typeof(InvalidOperationException).FullName);
            peerEvent.Payload[1].ShouldBe(failure.Wrapped.Message);
            totalAfter.Value.ShouldBe(totalBefore.Value);
            abortedAfter.Value.ShouldBe(abortedBefore.Value);
            events.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            writeAgain.TrySetResult();
        }
    }

    private static async Task<EventSourceRecorder> CreateErrorHandlingRecorderAsync(CancellationToken cancellationToken)
    {
        // A separate exchange initializes the process-wide source through its public middleware.
        // Its fault precedes the recorder and every counter baseline; no cross-assembly grant is needed.
        await using WebApplicationTestFactory warmup = new();
        warmup.Application.UseErrorHandling();
        warmup.Application.Use((_, _) => throw new InvalidOperationException("Initialize application-fault diagnostics."));
        await warmup.StartAsync(cancellationToken);
        using var client = warmup.CreateClient();
        using var response = await client.GetAsync($"/error-source-warmup/{Guid.NewGuid():N}", cancellationToken);
        ((int)response.StatusCode).ShouldBe(500);
        await warmup.StopAsync(cancellationToken);

        EventSource source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Web.ErrorHandling");
        return new EventSourceRecorder(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
    }
}
