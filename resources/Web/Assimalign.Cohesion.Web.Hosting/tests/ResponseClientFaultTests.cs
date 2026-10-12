using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>Client-caused response failures remain cancellation when a writer wraps the exception.</summary>
[Collection(nameof(TelemetryCollection))]
public class ResponseClientFaultTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Response client fault: A slow reader or peer reset should cancel through ErrorHandling even when its write failure is wrapped")]
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
        using TelemetryRecorder recorder = new(metrics: false);
        RecordingLoggerProvider recorded = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writeAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource peerCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Exception?> boundaryResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(InvalidOperationException Wrapped, CancellationToken Token, bool? Latched)> writeFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = $"/response-client-fault/{Guid.NewGuid():N}";
        int exceptionCallbacks = 0;
        int errorHandlers = 0;
        int completionCallbacks = 0;
        HttpMinDataRate rate = new(bytesPerSecond: 1_000_000, gracePeriod: TimeSpan.FromMilliseconds(100));

        await using ResponseBackpressureTransport transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Logging.AddProvider(recorded);
        factory.Builder.Services.AddErrorHandling(errors => errors.OnError((_, _, _) =>
        {
            Interlocked.Increment(ref errorHandlers);
            return ValueTask.FromResult(true);
        }));
        factory.Builder.Server.UseServer(options =>
        {
            options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
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
        factory.Application.UseErrorHandling(options => options.OnException = (_, _) =>
        {
            Interlocked.Increment(ref exceptionCallbacks);
            return ValueTask.CompletedTask;
        });
        factory.Application.Use(async (context, _) =>
        {
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
        await using Connection? http1 = protocol == 1
            ? await transport.Connections.CreateFactory().ConnectAsync(transport.Connections.EndPoint, cancellationToken)
            : null;
        await using Http2RawClient? http2 = protocol == 2
            ? await Http2RawClient.ConnectAsync(transport.Connections, cancellationToken)
            : null;
        await using Http3RawClient? http3 = protocol == 3
            ? await Http3RawClient.ConnectAsync(transport.MultiplexedConnections, cancellationToken)
            : null;
        await using Connection? requestStream = http3 is not null
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
                ((IMultiplexedStreamAbort)requestStream!).AbortRead(0x10c);
            }
            writeAgain.TrySetResult();

            var failure = await writeFailed.Task.WaitAsync(cancellationToken);
            Exception? result = await boundaryResult.Task.WaitAsync(cancellationToken);
            Activity span = await recorder.WaitForStoppedAsync(activity => Equals(activity.GetTagItem("url.path"), path), cancellationToken);
            await factory.StopAsync(cancellationToken);

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
        }
        finally
        {
            writeAgain.TrySetResult();
        }
    }
}
