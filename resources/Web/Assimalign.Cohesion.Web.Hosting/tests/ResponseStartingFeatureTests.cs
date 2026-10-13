using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Exercises the server-published response-starting hook through real head writers. HTTP/1.1 and
/// HTTP/2 use Web.Testing's in-memory client; HTTP/3 uses its multiplexed in-memory transport and a
/// raw QPACK client, so all three protocols run even on machines without QUIC support.
/// </summary>
public class ResponseStartingFeatureTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static readonly HttpHeaderKey _order = new("x-starting-order");
    private static readonly HttpHeaderKey _security = new("x-starting-security");
    private static readonly HttpHeaderKey _invalid = new("x-invalid");

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Buffered and streamed heads await callbacks once in reverse registration order")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_HeadCommit_ShouldAwaitCallbacksOnceInReverseOrder(int protocol, bool streamed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        List<int> calls = [];
        object firstState = new();
        object secondState = new();

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()!;
            starting.ShouldNotBeNull();
            starting.OnStarting(async state =>
            {
                state.ShouldBeSameAs(firstState);
                await Task.Yield();
                calls.Add(1);
                context.Response.Headers[_order] = string.Join(",", calls);
            }, firstState);
            starting.OnStarting(async state =>
            {
                state.ShouldBeSameAs(secondState);
                await Task.Yield();
                calls.Add(2);
                context.Response.Headers[_order] = string.Join(",", calls);
            }, secondState);

            calls.ShouldBeEmpty();
            context.Response.StatusCode = HttpStatusCode.Ok;

            if (streamed)
            {
                IHttpResponseStreamingFeature streaming = context.Response.Streaming;
                await streaming.StartAsync(cancellation.Token);
                calls.ShouldBe(new[] { 2, 1 });
                await streaming.StartAsync(cancellation.Token);
                await streaming.WriteAsync(Encoding.ASCII.GetBytes("body"), cancellation.Token);
                await streaming.FlushAsync(cancellation.Token);
                await streaming.CompleteAsync(cancellation.Token);
            }
            else
            {
                await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("body"), cancellation.Token);
                calls.ShouldBeEmpty();
            }
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(200);
        response.HeadCount.ShouldBe(1);
        response.Headers["x-starting-order"].ShouldBe("2,1");
        response.Body.ShouldBe("body");
        calls.ShouldBe(new[] { 2, 1 });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Registration after a streamed head commits throws")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_AfterHeadCommit_ShouldRejectRegistration(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        Exception? registrationFailure = null;
        int lateCalls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()!;
            starting.ShouldNotBeNull();
            await context.Response.Streaming.StartAsync(cancellation.Token);
            registrationFailure = Should.Throw<InvalidOperationException>(() => starting.OnStarting(_ =>
            {
                lateCalls++;
                return Task.CompletedTask;
            }, null));
            await context.Response.Streaming.CompleteAsync(cancellation.Token);
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(200);
        registrationFailure.ShouldBeOfType<InvalidOperationException>();
        lateCalls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A throwing callback faults before any response head reaches the client")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_ThrowingCallback_ShouldAbortBeforeWritingTheHead(int protocol, bool streamed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        int calls = 0;
        int completionCalls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Features.Get<IWebResponseCompletionFeature>()!.Register(() =>
            {
                completionCalls++;
                return ValueTask.CompletedTask;
            });
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(async _ =>
            {
                await Task.Yield();
                calls++;
                throw new InvalidOperationException("Deliberate response-starting callback failure.");
            }, null);
            context.Response.Headers[_security] = "must-not-reach-client";

            if (streamed)
            {
                await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("body"), cancellation.Token);
            }
            else
            {
                await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("body"), cancellation.Token);
            }
        }, cancellation.Token);

        // Assert
        calls.ShouldBe(1);
        completionCalls.ShouldBe(0);
        response.Failure.ShouldNotBeNull();
        response.Status.ShouldBeNull();
        response.HeadCount.ShouldBe(0);
        response.Headers.ShouldBeEmpty();
        response.Body.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Catching a streamed callback failure cannot bypass it with a buffered response")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_CaughtCallbackFailure_ShouldAbortTheBufferedRetry(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        InvalidOperationException callbackFailure = new("Deliberate callback failure caught by middleware.");
        Exception? caught = null;
        int calls = 0;
        int completionCalls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Features.Get<IWebResponseCompletionFeature>()!.Register(() =>
            {
                completionCalls++;
                return ValueTask.CompletedTask;
            });
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                calls++;
                throw callbackFailure;
            }, null);

            caught = await Should.ThrowAsync<InvalidOperationException>(
                () => context.Response.Streaming.StartAsync(cancellation.Token).AsTask());
            context.Response.Headers.Clear();
            context.Response.StatusCode = HttpStatusCode.Ok;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("must-not-reach-client"));
        }, cancellation.Token);

        // Assert
        caught.ShouldBeSameAs(callbackFailure);
        calls.ShouldBe(1);
        completionCalls.ShouldBe(0);
        response.Failure.ShouldNotBeNull();
        response.Status.ShouldBeNull();
        response.HeadCount.ShouldBe(0);
        response.Body.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A refused head preserves callback-added and changed headers on the server replacement without running callbacks twice")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_RefusedHead_ShouldPreserveCallbackHeadersOnServerReplacement(int protocol, bool streamed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            StageRefusedResponse(context, () => calls++);

            if (streamed)
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            else
            {
                await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("refused body"), cancellation.Token);
            }
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(500);
        response.HeadCount.ShouldBe(1);
        response.Headers["x-starting-security"].ShouldBe("nosniff");
        response.Headers["vary"].ShouldBe("Accept-Encoding, Origin");
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        response.Headers.ContainsKey("x-unrelated").ShouldBeFalse();
        response.Body.ShouldBeEmpty();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A middleware replacement after a refused streamed head retains callback headers without repeating callbacks")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_RefusedStreamedHead_ShouldPreserveCallbackHeadersOnMiddlewareReplacement(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        int calls = 0;
        HttpErrorCode? refusalCode = null;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            StageRefusedResponse(context, () => calls++);
            HttpException refused = await Should.ThrowAsync<HttpException>(
                () => context.Response.Streaming.StartAsync(cancellation.Token).AsTask());
            refusalCode = refused.Code;
            context.Response.Headers.Clear();
            context.Response.StatusCode = HttpStatusCode.BadGateway;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("replacement"));
        }, cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        response.HeadCount.ShouldBe(1);
        response.Headers["x-starting-security"].ShouldBe("nosniff");
        response.Headers["vary"].ShouldBe("Accept-Encoding, Origin");
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        response.Body.ShouldBe("replacement");
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Declared unchanged headers survive refusal and preservation keys are copied at registration")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_PreservedHeaderCollectionChangedAfterRegistration_ShouldKeepOnlyTheRegisteredHeader(int protocol, bool streamed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpHeaderKey preserved = new("x-preserved-policy");
        HttpHeaderKey unrelated = new("x-unrelated-application");
        List<HttpHeaderKey> preservedHeaders = [preserved];
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            context.Response.Headers[preserved] = "accepted-application-value";
            context.Response.Headers[unrelated] = "discarded-application-value";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                // A no-clobber policy accepts an existing header without changing its value.
                calls++;
                return Task.CompletedTask;
            }, null, preservedHeaders);

            // Registration owns a snapshot of these keys. Later caller mutations must neither drop
            // a declared policy header nor preserve an unrelated header from the failed response.
            preservedHeaders.Clear();
            preservedHeaders.Add(unrelated);

            if (streamed)
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            else
            {
                await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("refused body"), cancellation.Token);
            }
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(500);
        response.HeadCount.ShouldBe(1);
        response.Headers["x-preserved-policy"].ShouldBe("accepted-application-value");
        response.Headers.ContainsKey("x-unrelated-application").ShouldBeFalse();
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        response.Body.ShouldBeEmpty();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Declared header absence suppresses a buffered replacement's new header")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_PreservedAbsentHeader_ShouldRemoveTheMiddlewareReplacementHeader(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpHeaderKey absent = new("x-preserved-absence");
        HttpErrorCode? refusalCode = null;
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                // The key is absent both before and after the callback, so a delta alone cannot
                // distinguish this policy decision from a header the callback never governed.
                calls++;
                return Task.CompletedTask;
            }, null, [absent]);

            try
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            catch (HttpException refused)
            {
                refusalCode = refused.Code;
            }

            context.Response.Headers.Clear();
            context.Response.Headers[absent] = "replacement default";
            context.Response.StatusCode = HttpStatusCode.BadGateway;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("replacement"));
        }, cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        response.HeadCount.ShouldBe(1);
        response.Headers.ContainsKey("x-preserved-absence").ShouldBeFalse();
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        response.Body.ShouldBe("replacement");
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A callback's header removal survives a cleared-head replacement")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_RemovedHeader_ShouldRemainRemovedOnMiddlewareReplacement(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpHeaderKey removed = new("x-remove-on-starting");
        HttpErrorCode? refusalCode = null;
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            context.Response.Headers[removed] = "original";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                calls++;
                context.Response.Headers.Remove(removed);
                return Task.CompletedTask;
            }, null);

            try
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            catch (HttpException refused)
            {
                refusalCode = refused.Code;
            }

            context.Response.Headers.Clear();
            // A replacement response can reintroduce a default the callback explicitly removed.
            // Replaying its removal must suppress that field without invoking the callback again.
            context.Response.Headers[removed] = "replacement default";
            context.Response.StatusCode = HttpStatusCode.BadGateway;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("replacement"));
        }, cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        response.HeadCount.ShouldBe(1);
        response.Headers.ContainsKey("x-remove-on-starting").ShouldBeFalse();
        response.Body.ShouldBe("replacement");
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Registration is closed during callbacks and remains closed after a refused head")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_DuringCallbackAndAfterRefusal_ShouldRejectRegistration(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        Exception? duringCallback = null;
        Exception? afterRefusal = null;
        HttpErrorCode? refusalCode = null;
        int calls = 0;
        int lateCalls = 0;
        Func<object?, Task> lateCallback = _ =>
        {
            lateCalls++;
            return Task.CompletedTask;
        };

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>()!;
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            starting.OnStarting(_ =>
            {
                calls++;

                try
                {
                    starting.OnStarting(lateCallback, null);
                }
                catch (InvalidOperationException exception)
                {
                    duringCallback = exception;
                }

                return Task.CompletedTask;
            }, null);

            try
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            catch (HttpException refused)
            {
                refusalCode = refused.Code;
            }

            try
            {
                starting.OnStarting(lateCallback, null);
            }
            catch (InvalidOperationException exception)
            {
                afterRefusal = exception;
            }

            context.Response.Headers.Clear();
            context.Response.StatusCode = HttpStatusCode.BadGateway;
        }, cancellation.Token);

        // Assert
        duringCallback.ShouldBeOfType<InvalidOperationException>();
        afterRefusal.ShouldBeOfType<InvalidOperationException>();
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        calls.ShouldBe(1);
        lateCalls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: A throwing cancellation callback does not replace the original response-starting failure")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnStarting_ThrowingCancellationCallback_ShouldPreserveTheStartingFailure(int protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        InvalidOperationException startingFailure = new("Original starting failure.");
        InvalidOperationException cancellationFailure = new("Secondary cancellation callback failure.");
        Exception? caught = null;
        int startingCalls = 0;
        int cancellationCalls = 0;
        int completionCalls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Features.Get<IWebResponseCompletionFeature>()!.Register(() =>
            {
                completionCalls++;
                return ValueTask.CompletedTask;
            });
            using CancellationTokenRegistration registration = context.RequestCancelled.Register(() =>
            {
                cancellationCalls++;
                throw cancellationFailure;
            });
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                startingCalls++;
                throw startingFailure;
            }, null);

            try
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            catch (InvalidOperationException exception)
            {
                caught = exception;
            }
        }, cancellation.Token);

        // Assert — these observations are checked outside the server's exception boundary.
        caught.ShouldBeSameAs(startingFailure);
        startingCalls.ShouldBe(1);
        cancellationCalls.ShouldBe(1);
        completionCalls.ShouldBe(0);
        response.Failure.ShouldNotBeNull();
        response.Status.ShouldBeNull();
        response.HeadCount.ShouldBe(0);
        response.Body.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Streaming: A refused head stays unstarted and preserves starting headers on a buffered replacement")]
    [InlineData(1, "start")]
    [InlineData(1, "write")]
    [InlineData(1, "flush")]
    [InlineData(1, "complete")]
    [InlineData(2, "start")]
    [InlineData(2, "write")]
    [InlineData(2, "flush")]
    [InlineData(2, "complete")]
    [InlineData(3, "start")]
    [InlineData(3, "write")]
    [InlineData(3, "flush")]
    [InlineData(3, "complete")]
    public async Task StreamingOperation_RefusedHead_ShouldStayUnstartedForBufferedReplacement(int protocol, string operation)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpErrorCode? refusalCode = null;
        bool? featureStartedInCallback = null;
        bool? exchangeStartedInCallback = null;
        bool? featureStartedAfterRefusal = null;
        bool? exchangeStartedAfterRefusal = null;
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;
            StageRefusedResponse(context, () => calls++);
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                // Record rather than assert inside the hook: a failed assertion would itself
                // abort the exchange before the transport's InvalidResponseField refusal.
                featureStartedInCallback = streaming.HasStarted;
                exchangeStartedInCallback = context.HasResponseStarted;
                return Task.CompletedTask;
            }, null);

            try
            {
                await InvokeStreamingOperationAsync(streaming, operation, cancellation.Token);
            }
            catch (HttpException refused)
            {
                refusalCode = refused.Code;
            }

            featureStartedAfterRefusal = streaming.HasStarted;
            exchangeStartedAfterRefusal = context.HasResponseStarted;
            context.Response.Headers.Clear();
            context.Response.StatusCode = HttpStatusCode.BadGateway;
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("replacement"));
        }, cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        featureStartedInCallback.ShouldBe(false);
        exchangeStartedInCallback.ShouldBe(false);
        featureStartedAfterRefusal.ShouldBe(false);
        exchangeStartedAfterRefusal.ShouldBe(false);
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        response.HeadCount.ShouldBe(1);
        response.Body.ShouldBe("replacement");
        response.Headers["x-starting-security"].ShouldBe("nosniff");
        response.Headers["vary"].ShouldBe("Accept-Encoding, Origin");
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Streaming: A refused operation can retry and completion forbids writes only after succeeding")]
    [InlineData(1, "start")]
    [InlineData(1, "write")]
    [InlineData(1, "flush")]
    [InlineData(1, "complete")]
    [InlineData(2, "start")]
    [InlineData(2, "write")]
    [InlineData(2, "flush")]
    [InlineData(2, "complete")]
    [InlineData(3, "start")]
    [InlineData(3, "write")]
    [InlineData(3, "flush")]
    [InlineData(3, "complete")]
    public async Task StreamingOperation_RefusedHead_ShouldAllowTheSameOperationToRetry(int protocol, string operation)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpErrorCode? refusalCode = null;
        bool? featureStartedInCallback = null;
        bool? featureStartedAfterRefusal = null;
        bool? exchangeStartedAfterRefusal = null;
        bool? featureStartedAfterRetry = null;
        bool? exchangeStartedAfterRetry = null;
        Exception? writeAfterCompletion = null;
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            IHttpResponseStreamingFeature streaming = context.Response.Streaming;
            StageRefusedResponse(context, () => calls++);
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                featureStartedInCallback = streaming.HasStarted;
                return Task.CompletedTask;
            }, null);

            try
            {
                await InvokeStreamingOperationAsync(streaming, operation, cancellation.Token);
            }
            catch (HttpException refused)
            {
                refusalCode = refused.Code;
            }

            featureStartedAfterRefusal = streaming.HasStarted;
            exchangeStartedAfterRefusal = context.HasResponseStarted;
            context.Response.Headers.Remove(_invalid);
            context.Response.StatusCode = HttpStatusCode.Ok;
            await InvokeStreamingOperationAsync(streaming, operation, cancellation.Token);
            featureStartedAfterRetry = streaming.HasStarted;
            exchangeStartedAfterRetry = context.HasResponseStarted;

            // Complete is idempotent after its successful call, including when it was itself
            // the retried operation. The write guard must now apply to every path.
            await streaming.CompleteAsync(cancellation.Token);
            await streaming.CompleteAsync(cancellation.Token);

            try
            {
                await streaming.WriteAsync(Encoding.ASCII.GetBytes("after completion"), cancellation.Token);
            }
            catch (InvalidOperationException exception)
            {
                writeAfterCompletion = exception;
            }
        }, cancellation.Token);

        // Assert
        refusalCode.ShouldBe(HttpErrorCode.InvalidResponseField);
        featureStartedInCallback.ShouldBe(false);
        featureStartedAfterRefusal.ShouldBe(false);
        exchangeStartedAfterRefusal.ShouldBe(false);
        featureStartedAfterRetry.ShouldBe(true);
        exchangeStartedAfterRetry.ShouldBe(true);
        writeAfterCompletion.ShouldBeOfType<InvalidOperationException>();
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(200);
        response.HeadCount.ShouldBe(1);
        response.Body.ShouldBe(operation == "write" ? "streamed" : string.Empty);
        response.Headers["x-starting-security"].ShouldBe("nosniff");
        response.Headers["vary"].ShouldBe("Accept-Encoding, Origin");
        response.Headers.ContainsKey("x-invalid").ShouldBeFalse();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Server replacements discard callback framing and representation metadata")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_CallbackBodyMetadata_ShouldNotReplayOnServerReplacement(int protocol, bool streamed)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        HttpHeaderKey[] representationHeaders =
        [
            new("Content-Type"), new("Content-Encoding"), new("Content-Language"),
            new("Content-Location"), new("Content-Range"), new("Content-Disposition"),
            new("Content-MD5"), new("Content-Digest"), new("Repr-Digest"), new("Digest"),
            new("ETag"), new("Last-Modified"), new("Accept-Ranges"), new("Trailer"),
        ];
        int calls = 0;

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                calls++;
                context.Response.Headers[HttpHeaderKey.ContentLength] = "4";
                foreach (HttpHeaderKey key in representationHeaders)
                {
                    context.Response.Headers[key] = "original";
                }
                context.Response.Headers[_security] = "nosniff";
                return Task.CompletedTask;
            }, null, [HttpHeaderKey.ContentLength, .. representationHeaders]);
            context.Response.StatusCode = HttpStatusCode.Ok;
            if (streamed)
            {
                await context.Response.Streaming.StartAsync(cancellation.Token);
            }
            else
            {
                await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("body"), cancellation.Token);
            }
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(500);
        response.Body.ShouldBeEmpty();
        response.Headers["content-length"].ShouldBe("0");
        foreach (HttpHeaderKey key in representationHeaders)
        {
            response.Headers.ContainsKey(key.Value).ShouldBeFalse(key.Value);
        }
        response.Headers["x-starting-security"].ShouldBe("nosniff");
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - OnStarting: Middleware replacements own metadata even when callbacks changed or removed the original fields")]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task OnStarting_CallbackBodyMetadata_ShouldNotOverrideMiddlewareReplacement(int protocol, bool remove)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);

        // Act
        ResponseSnapshot response = await SendAsync(protocol, async context =>
        {
            context.Response.Headers[_invalid] = "value\r\nx-injected: true";
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/original";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                context.Response.Headers[HttpHeaderKey.ContentLength] = "4";
                context.Response.Headers[HttpHeaderKey.ContentType] = remove ? HttpHeaderValue.Empty : "text/changed";
                return Task.CompletedTask;
            }, null);
            HttpException refused = await Should.ThrowAsync<HttpException>(
                () => context.Response.Streaming.StartAsync(cancellation.Token).AsTask());
            refused.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
            context.Response.Headers.Clear();
            context.Response.StatusCode = HttpStatusCode.BadGateway;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/replacement";
            context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("replacement"));
        }, cancellation.Token);

        // Assert
        response.Failure.ShouldBeNull();
        response.Status.ShouldBe(502);
        response.Body.ShouldBe("replacement");
        response.Headers["content-length"].ShouldBe("11");
        response.Headers["content-type"].ShouldBe("text/replacement");
    }

    private static ValueTask InvokeStreamingOperationAsync(
        IHttpResponseStreamingFeature streaming,
        string operation,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            "start" => streaming.StartAsync(cancellationToken),
            "write" => streaming.WriteAsync(Encoding.ASCII.GetBytes("streamed"), cancellationToken),
            "flush" => streaming.FlushAsync(cancellationToken),
            "complete" => streaming.CompleteAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static void StageRefusedResponse(IHttpContext context, Action recordCallback)
    {
        context.Response.StatusCode = HttpStatusCode.Found;
        context.Response.Headers[_invalid] = "value\r\nx-injected: true";
        context.Response.Headers[new HttpHeaderKey("x-unrelated")] = "discarded";
        context.Response.Headers[HttpHeaderKey.Vary] = "Accept-Encoding";
        context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
        {
            recordCallback();
            context.Response.Headers[_security] = "nosniff";
            context.Response.Headers[HttpHeaderKey.Vary] = "Accept-Encoding, Origin";
            return Task.CompletedTask;
        }, null);
    }

    private static async Task<ResponseSnapshot> SendAsync(
        int protocol,
        Func<IHttpContext, Task> pipeline,
        CancellationToken cancellationToken)
    {
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = protocol == 2 ? WebApplicationTestProtocol.Http2 : WebApplicationTestProtocol.Http1,
        });
        factory.Builder.Server.UseServer(options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        InMemoryMultiplexedConnectionListener? multiplexed = protocol == 3 ? new() : null;

        if (multiplexed is not null)
        {
            factory.Builder.Server.UseServer(options => options.UseHttp3(multiplexed));
        }

        factory.Application.Use((context, _) => pipeline(context));

        if (multiplexed is not null)
        {
            await factory.StartAsync(cancellationToken);
            await using Http3RawClient client = await Http3RawClient.ConnectAsync(multiplexed, cancellationToken);
            await using IDuplexConnection control = await client.OpenControlStreamAsync(cancellationToken);
            await using IDuplexConnection stream = await client.SendRequestAsync("POST", "/", [], [], endStream: true, cancellationToken);
            return await ReadHttp3ResponseAsync(stream, cancellationToken);
        }

        using HttpClient httpClient = factory.CreateClient();
        ResponseSnapshot snapshot = new();

        try
        {
            // A nonempty request body prevents the HTTP/1.1 client from retrying a connection
            // closed without a response, which would otherwise create extra exchanges.
            using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Post, "/")
            {
                Version = httpClient.DefaultRequestVersion,
                VersionPolicy = httpClient.DefaultVersionPolicy,
                Content = new StringContent("request"),
            };
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            snapshot.Status = (int)response.StatusCode;
            snapshot.HeadCount = 1;

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
            {
                snapshot.Headers[header.Key] = string.Join(", ", header.Value);
            }

            snapshot.Body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            snapshot.Failure = exception;
        }

        return snapshot;
    }

    private static async Task<ResponseSnapshot> ReadHttp3ResponseAsync(IDuplexConnection stream, CancellationToken cancellationToken)
    {
        ResponseSnapshot snapshot = new();
        List<byte> received = [];

        try
        {
            while (true)
            {
                ReadResult read = await stream.Input.ReadAsync(cancellationToken);

                foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                {
                    received.AddRange(segment.ToArray());
                }

                stream.Input.AdvanceTo(read.Buffer.End);

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (ConnectionResetException exception)
        {
            snapshot.Failure = exception;
        }

        byte[] bytes = received.ToArray();
        int index = 0;

        while (index < bytes.Length)
        {
            QPackTestCodec.TryReadVariableLengthInteger(bytes, ref index, out long type).ShouldBeTrue();
            QPackTestCodec.TryReadVariableLengthInteger(bytes, ref index, out long length).ShouldBeTrue();
            ReadOnlySpan<byte> payload = bytes.AsSpan(index, checked((int)length));
            index += checked((int)length);

            if (type == QPackTestCodec.HeadersFrame)
            {
                snapshot.HeadCount++;

                foreach ((string name, string value) in QPackTestCodec.DecodeFieldSection(payload))
                {
                    if (name == ":status")
                    {
                        snapshot.Status = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        snapshot.Headers[name] = value;
                    }
                }
            }
            else if (type == QPackTestCodec.DataFrame)
            {
                snapshot.Body += Encoding.ASCII.GetString(payload);
            }
        }

        return snapshot;
    }

    private sealed class ResponseSnapshot
    {
        public int? Status { get; set; }

        public int HeadCount { get; set; }

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Body { get; set; } = string.Empty;

        public Exception? Failure { get; set; }
    }
}
