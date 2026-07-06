using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Verifies the last-chance exception boundary registered by <c>UseExceptionHandler()</c>: it catches
/// pipeline faults, publishes the typed <see cref="IHttpExceptionFeature"/>, honors the developer-detail
/// toggle and diagnostics-suppression callback, runs the ordered handler chain, and falls back to a
/// safe 500 problem+json.
/// </summary>
public class ExceptionHandlerMiddlewareTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: converts an unhandled exception into a 500 problem+json")]
    public async Task Invoke_WhenPipelineThrows_WritesSafe500ProblemJson()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler();
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("boom"));

        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(500);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(500);
        root.GetProperty("title").GetString().ShouldBe("Internal Server Error");
        // No developer detail by default — internals must not leak.
        root.TryGetProperty("detail", out _).ShouldBeFalse();
        root.TryGetProperty("exception", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: publishes the caught exception as a typed feature")]
    public async Task Invoke_WhenPipelineThrows_PublishesExceptionFeature()
    {
        TestHttpContext context = new(HttpPath.Root.Concat(new HttpPath("/orders")));
        var thrown = new InvalidOperationException("boom");

        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler();
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw thrown);

        await builder.Build().ExecuteAsync(context);

        IHttpExceptionFeature? feature = context.Features.Get<IHttpExceptionFeature>();
        feature.ShouldNotBeNull();
        feature!.Error.ShouldBeSameAs(thrown);
        feature.Path.Value.ShouldBe("/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: developer detail on echoes message and exception text")]
    public async Task Invoke_WithDeveloperDetails_IncludesMessageAndExceptionText()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options => options.IncludeDeveloperDetails = true);
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("kaboom"));

        await builder.Build().ExecuteAsync(context);

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        JsonElement root = document.RootElement;
        root.GetProperty("detail").GetString().ShouldBe("kaboom");
        root.GetProperty("exception").GetString()!.ShouldContain("InvalidOperationException", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: a handler that handles the fault stops the chain and owns the response")]
    public async Task Invoke_WhenHandlerHandles_SkipsFallback()
    {
        TestHttpContext context = new();
        var forbidden = new StatusWritingHandler(handled: true, status: 403);

        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options => options.Handlers.Add(forbidden));
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("boom"));

        await builder.Build().ExecuteAsync(context);

        forbidden.Invoked.ShouldBeTrue();
        context.Response.StatusCode.Value.ShouldBe(403);
        // The fallback never ran, so no problem+json content type was written.
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: handlers are tried in registration order")]
    public async Task Invoke_WithMultipleHandlers_TriesInOrder()
    {
        TestHttpContext context = new();
        var log = new List<string>();
        var first = new RecordingHandler("first", handled: false, log);
        var second = new RecordingHandler("second", handled: true, log);
        var third = new RecordingHandler("third", handled: true, log);

        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options =>
        {
            options.Handlers.Add(first);
            options.Handlers.Add(second);
            options.Handlers.Add(third);
        });
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("boom"));

        await builder.Build().ExecuteAsync(context);

        // first defers, second handles, third never runs.
        log.ShouldBe(new[] { "first", "second" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: a handler that throws falls through to the fallback")]
    public async Task Invoke_WhenHandlerThrows_FallsThroughToFallback()
    {
        TestHttpContext context = new();
        var faulty = new ThrowingHandler();

        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options => options.Handlers.Add(faulty));
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("boom"));

        await builder.Build().ExecuteAsync(context);

        faulty.Invoked.ShouldBeTrue();
        context.Response.StatusCode.Value.ShouldBe(500);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: custom fallback status is honored")]
    public async Task Invoke_WithCustomFallbackStatus_UsesConfiguredStatus()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options => options.StatusCode = HttpStatusCode.ServiceUnavailable);
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("boom"));

        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(503);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(503);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: a partial inner response is discarded before the error body")]
    public async Task Invoke_WhenInnerWrotePartialBody_ResetsBeforeFallback()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler();
        builder.Use(async (ctx, _) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.Ok;
            ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
            byte[] partial = System.Text.Encoding.UTF8.GetBytes("half-written garbage");
            await ctx.Response.Body.WriteAsync(partial);
            throw new InvalidOperationException("boom after partial write");
        });

        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(500);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        // The partial text was truncated; only clean problem+json remains.
        string body = context.ResponseBodyText();
        body.ShouldNotContain("garbage");
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: a cancelled-request OperationCanceledException is not converted to 500")]
    public async Task Invoke_WhenRequestCancelled_RethrowsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        TestHttpContext context = new() { RequestCancelled = cts.Token };

        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler();
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => builder.Build().ExecuteAsync(context));
        context.Response.StatusCode.Value.ShouldBe(200); // untouched — no error body manufactured
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: suppression callback flags the exchange for diagnostics")]
    public async Task Invoke_WhenSuppressionCallbackReturnsTrue_SetsDiagnosticsFlag()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler(options => options.SuppressDiagnosticsCallback = (_, ex) => ex is InvalidOperationException);
        builder.Use((IHttpContext _, WebApplicationMiddleware _) => throw new InvalidOperationException("expected"));

        await builder.Build().ExecuteAsync(context);

        context.Items.TryGetValue(ExceptionHandlerOptions.DiagnosticsSuppressedItemKey, out object? flag).ShouldBeTrue();
        flag.ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseExceptionHandler: no fault leaves the response untouched")]
    public async Task Invoke_WhenNoException_PassesThrough()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseExceptionHandler();
        builder.Use((ctx, next) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.Ok;
            return next.Invoke(ctx);
        });

        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(200);
        context.Features.Get<IHttpExceptionFeature>().ShouldBeNull();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    private sealed class StatusWritingHandler : IExceptionHandler
    {
        private readonly bool _handled;
        private readonly int _status;

        public StatusWritingHandler(bool handled, int status)
        {
            _handled = handled;
            _status = status;
        }

        public bool Invoked { get; private set; }

        public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            Invoked = true;
            if (_handled)
            {
                context.Response.StatusCode = _status;
            }

            return ValueTask.FromResult(_handled);
        }
    }

    private sealed class RecordingHandler : IExceptionHandler
    {
        private readonly string _name;
        private readonly bool _handled;
        private readonly List<string> _log;

        public RecordingHandler(string name, bool handled, List<string> log)
        {
            _name = name;
            _handled = handled;
            _log = log;
        }

        public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            _log.Add(_name);
            return ValueTask.FromResult(_handled);
        }
    }

    private sealed class ThrowingHandler : IExceptionHandler
    {
        public bool Invoked { get; private set; }

        public ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            Invoked = true;
            throw new InvalidOperationException("handler failed");
        }
    }
}
