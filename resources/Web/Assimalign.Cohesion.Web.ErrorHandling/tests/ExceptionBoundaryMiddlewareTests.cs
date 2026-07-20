using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>
/// Coverage for the exception-boundary middleware (<c>UseErrorHandling</c>): fault capture and the
/// <see cref="IHttpExceptionFeature"/> publication, dispatch through the shipped <c>OnError</c> chain,
/// the no-clobber guard, the developer-detail toggle, and the diagnostics-observation hook. The verb
/// is driven over a minimal pipeline harness so the middleware runs exactly as the runtime composes it.
/// </summary>
public class ExceptionBoundaryMiddlewareTests
{
    private static IErrorHandlingFeature Hook(Action<ErrorHandlingBuilder>? configure = null)
    {
        TestWebApplicationBuilder builder = new();
        ErrorHandlingBuilder composition = builder.AddErrorHandling();
        configure?.Invoke(composition);
        return builder.Features.OfType<IErrorHandlingFeature>().Single();
    }

    private static async Task<TestHttpContext> RunAsync(
        Action<ExceptionBoundaryOptions>? configure,
        Func<IHttpContext, Task> downstream,
        IErrorHandlingFeature? hook = null,
        IHttpResponseStreamingFeature? streaming = null)
    {
        TestPipelineBuilder builder = new();
        builder.UseErrorHandling(configure);
        builder.Run(downstream);
        IWebApplicationPipeline pipeline = builder.Build();

        TestHttpContext context = new();
        if (hook is not null)
        {
            context.Features.Set(hook);
        }

        if (streaming is not null)
        {
            context.Features.Set(streaming);
        }

        await pipeline.ExecuteAsync(context);
        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should publish the caught fault as an IHttpExceptionFeature")]
    public async Task InvokeAsync_DownstreamThrows_ShouldPublishExceptionFeature()
    {
        // Arrange
        InvalidOperationException fault = new("boom");

        // Act
        TestHttpContext context = await RunAsync(null, _ => throw fault);

        // Assert
        IHttpExceptionFeature? feature = context.Features.Get<IHttpExceptionFeature>();
        feature.ShouldNotBeNull();
        feature.Error.ShouldBeSameAs(fault);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should render an escaped fault as problem+json without leaking internals")]
    public async Task InvokeAsync_NoHandler_ShouldRenderProblemJson500()
    {
        // Act
        TestHttpContext context = await RunAsync(null, _ => throw new InvalidOperationException("secret internals"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.GetProperty("title").GetString().ShouldBe("Internal Server Error");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        context.ResponseBodyText().ShouldNotContain("secret internals", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should let a registered OnError handler own the fault response")]
    public async Task InvokeAsync_RegisteredHandlerOwns_ShouldUseHandlerResponse()
    {
        // Arrange
        IErrorHandlingFeature hook = Hook(composition => composition.OnError(async (context, exception, token) =>
        {
            context.Response.StatusCode = 503;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync("owned"u8.ToArray(), token);
            return true;
        }));

        // Act
        TestHttpContext context = await RunAsync(null, _ => throw new TimeoutException("upstream"), hook);

        // Assert
        context.Response.StatusCode.ShouldBe((HttpStatusCode)503);
        context.ResponseBodyText().ShouldBe("owned");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should consult OnError handlers in registration order, first-true wins")]
    public async Task InvokeAsync_MultipleHandlers_ShouldConsultInOrderFirstTrueWins()
    {
        // Arrange
        RecordingErrorHandler first = new(handles: false);
        RecordingErrorHandler second = new(handles: true);
        RecordingErrorHandler third = new(handles: false);
        IErrorHandlingFeature hook = Hook(composition =>
        {
            composition.OnError(first);
            composition.OnError(second);
            composition.OnError(third);
        });

        // Act
        await RunAsync(null, _ => throw new InvalidOperationException("boom"), hook);

        // Assert
        first.WasConsulted.ShouldBeTrue();
        second.WasConsulted.ShouldBeTrue();
        third.WasConsulted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should render the terminal default when every handler passes")]
    public async Task InvokeAsync_AllHandlersPass_ShouldRenderTerminalDefault()
    {
        // Arrange
        IErrorHandlingFeature hook = Hook(composition => composition.OnError(new RecordingErrorHandler(handles: false)));

        // Act
        TestHttpContext context = await RunAsync(null, _ => throw new InvalidOperationException("boom"), hook);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.ResponseBodyText().ShouldContain("about:blank", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should propagate a fault thrown by an OnError handler")]
    public async Task InvokeAsync_HandlerThrows_ShouldPropagate()
    {
        // Arrange
        IErrorHandlingFeature hook = Hook(composition =>
            composition.OnError((context, exception, token) => throw new NotSupportedException("handler fault")));

        TestPipelineBuilder builder = new();
        builder.UseErrorHandling();
        builder.Run(_ => throw new InvalidOperationException("boom"));
        IWebApplicationPipeline pipeline = builder.Build();

        TestHttpContext context = new();
        context.Features.Set(hook);

        // Act / Assert — handler faults are not masked; they surface to the invoking boundary's caller.
        await Should.ThrowAsync<NotSupportedException>(async () => await pipeline.ExecuteAsync(context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should abort the exchange when the response has already started")]
    public async Task InvokeAsync_ResponseStarted_ShouldAbortWithoutClobber()
    {
        // Act
        TestHttpContext context = await RunAsync(
            null,
            _ => throw new InvalidOperationException("boom"),
            hook: null,
            streaming: new FakeResponseStreamingFeature(hasStarted: true));

        // Assert
        context.CancelRequested.ShouldBeTrue();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should reset a partial response before rendering the error")]
    public async Task InvokeAsync_PartialResponseWritten_ShouldResetBeforeRender()
    {
        // Act
        TestHttpContext context = await RunAsync(null, ctx =>
        {
            ctx.Response.StatusCode = (HttpStatusCode)200;
            ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
            ctx.Response.Body.Write("partial-body"u8);
            throw new InvalidOperationException("boom");
        });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("application/problem+json");
        context.ResponseBodyText().ShouldNotContain("partial-body", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should include exception detail when developer details are enabled")]
    public async Task InvokeAsync_DeveloperDetailsEnabled_ShouldIncludeDetail()
    {
        // Act
        TestHttpContext context = await RunAsync(
            options => options.IncludeDeveloperDetails = true,
            _ => throw new InvalidOperationException("kaboom-detail"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("detail").GetString().ShouldBe("kaboom-detail");
        document.RootElement.GetProperty("exception").GetString()!.ShouldContain("InvalidOperationException", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should omit exception detail by default")]
    public async Task InvokeAsync_DeveloperDetailsDisabled_ShouldOmitDetail()
    {
        // Act
        TestHttpContext context = await RunAsync(null, _ => throw new InvalidOperationException("kaboom-detail"));

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.TryGetProperty("exception", out _).ShouldBeFalse();
        context.ResponseBodyText().ShouldNotContain("kaboom-detail", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should invoke the diagnostics observer for an unsuppressed fault")]
    public async Task InvokeAsync_NotSuppressed_ShouldInvokeObserver()
    {
        // Arrange
        InvalidOperationException fault = new("boom");
        Exception? observed = null;

        // Act
        await RunAsync(
            options => options.OnException = (context, exception) =>
            {
                observed = exception;
                return ValueTask.CompletedTask;
            },
            _ => throw fault);

        // Assert
        observed.ShouldBeSameAs(fault);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should skip the diagnostics observer when suppression marks the fault expected")]
    public async Task InvokeAsync_Suppressed_ShouldSkipObserver()
    {
        // Arrange
        bool observed = false;

        // Act
        TestHttpContext context = await RunAsync(
            options =>
            {
                options.SuppressDiagnosticsCallback = (_, _) => true;
                options.OnException = (_, _) =>
                {
                    observed = true;
                    return ValueTask.CompletedTask;
                };
            },
            _ => throw new InvalidOperationException("boom"));

        // Assert — suppression skips the observer, but the fault is still handled.
        observed.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: A throwing diagnostics observer should not defeat response rendering")]
    public async Task InvokeAsync_ObserverThrows_ShouldStillRenderResponse()
    {
        // Act
        TestHttpContext context = await RunAsync(
            options => options.OnException = (_, _) => throw new InvalidOperationException("observer boom"),
            _ => throw new InvalidOperationException("boom"));

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.ResponseBodyText().ShouldContain("about:blank", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: A cancelled request should propagate as a clean drain, not a fault")]
    public async Task InvokeAsync_OperationCanceledWhileRequestCancelled_ShouldRethrow()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        TestPipelineBuilder builder = new();
        builder.UseErrorHandling();
        builder.Run(_ => throw new OperationCanceledException());
        IWebApplicationPipeline pipeline = builder.Build();

        TestHttpContext context = new() { RequestCancelled = cancellation.Token };

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync(context));
        context.ResponseBodyText().ShouldBeEmpty();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary: Should pass a fault-free request straight through")]
    public async Task InvokeAsync_NoFault_ShouldPassThrough()
    {
        // Act
        TestHttpContext context = await RunAsync(null, ctx =>
        {
            ctx.Response.StatusCode = (HttpStatusCode)204;
            return Task.CompletedTask;
        });

        // Assert
        context.Response.StatusCode.ShouldBe((HttpStatusCode)204);
        context.ResponseBodyText().ShouldBeEmpty();
        context.Features.Get<IHttpExceptionFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseErrorHandling: Should reject a null pipeline builder")]
    public void UseErrorHandling_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseErrorHandling());
    }
}
