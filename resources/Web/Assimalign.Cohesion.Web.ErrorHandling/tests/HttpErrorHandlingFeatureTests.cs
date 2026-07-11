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
/// The <c>OnError</c> chain semantics: registration-order consultation, first-handled-wins, the
/// terminal <c>ProblemDetails</c> default, and fault propagation out of a broken handler.
/// </summary>
public class HttpErrorHandlingFeatureTests
{
    private static IHttpErrorHandlingFeature Compose(TestWebApplicationBuilder builder, params IHttpErrorHandler[] handlers)
    {
        ErrorHandlingBuilder composition = builder.AddErrorHandling();

        foreach (IHttpErrorHandler handler in handlers)
        {
            composition.OnError(handler);
        }

        return builder.Features.OfType<IHttpErrorHandlingFeature>().Single();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - AddErrorHandling: Should attach the hook feature to the application")]
    public void AddErrorHandling_OnBuilder_ShouldAttachHookFeature()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();

        // Act
        builder.AddErrorHandling();

        // Assert
        builder.Features.OfType<IHttpErrorHandlingFeature>().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - OnError: Should expose handlers in registration order")]
    public void OnError_MultipleHandlers_ShouldExposeInRegistrationOrder()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        RecordingErrorHandler first = new(handles: false);
        RecordingErrorHandler second = new(handles: true);

        // Act
        IHttpErrorHandlingFeature feature = Compose(builder, first, second);

        // Assert
        feature.Handlers.Count.ShouldBe(2);
        feature.Handlers[0].ShouldBeSameAs(first);
        feature.Handlers[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - OnError: Should reject null handlers")]
    public void OnError_NullHandler_ShouldThrow()
    {
        // Arrange
        ErrorHandlingBuilder builder = new TestWebApplicationBuilder().AddErrorHandling();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.OnError((IHttpErrorHandler)null!));
        Should.Throw<ArgumentNullException>(() => builder.OnError((HttpErrorHandler)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should stop the chain at the first handler that owns the fault")]
    public async Task HandleAsync_FirstHandlerHandles_ShouldStopChain()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        RecordingErrorHandler first = new(handles: true);
        RecordingErrorHandler second = new(handles: false);
        IHttpErrorHandlingFeature feature = Compose(builder, first, second);
        TestHttpContext context = new();

        // Act
        await feature.HandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        // Assert
        first.WasConsulted.ShouldBeTrue();
        second.WasConsulted.ShouldBeFalse();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should consult handlers in order until one owns the fault")]
    public async Task HandleAsync_FirstPassesSecondHandles_ShouldConsultInOrder()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        RecordingErrorHandler first = new(handles: false);
        RecordingErrorHandler second = new(handles: true);
        IHttpErrorHandlingFeature feature = Compose(builder, first, second);
        TestHttpContext context = new();

        // Act
        await feature.HandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        // Assert
        first.WasConsulted.ShouldBeTrue();
        second.WasConsulted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should render the ProblemDetails default when every handler passes")]
    public async Task HandleAsync_AllHandlersPass_ShouldRenderProblemDetailsDefault()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        RecordingErrorHandler passer = new(handles: false);
        IHttpErrorHandlingFeature feature = Compose(builder, passer);
        TestHttpContext context = new();

        // Act
        await feature.HandleAsync(context, new InvalidOperationException("secret internals"), CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.GetProperty("title").GetString().ShouldBe("Internal Server Error");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);

        // The default never leaks fault internals.
        context.ResponseBodyText().ShouldNotContain("secret internals");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should render the default when no handlers are registered")]
    public async Task HandleAsync_NoHandlers_ShouldRenderDefault()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        IHttpErrorHandlingFeature feature = Compose(builder);
        TestHttpContext context = new();

        // Act
        await feature.HandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        context.ResponseBodyText().ShouldContain("about:blank", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should let a delegate registration own the fault")]
    public async Task HandleAsync_DelegateHandlerHandles_ShouldNotRunDefault()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        ErrorHandlingBuilder composition = builder.AddErrorHandling();
        composition.OnError((context, exception, cancellationToken) =>
        {
            context.Response.StatusCode = 503;
            return ValueTask.FromResult(true);
        });
        IHttpErrorHandlingFeature feature = builder.Features.OfType<IHttpErrorHandlingFeature>().Single();
        TestHttpContext context = new();

        // Act
        await feature.HandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe((HttpStatusCode)503);
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should propagate a fault thrown by a handler")]
    public async Task HandleAsync_HandlerThrows_ShouldPropagate()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        ErrorHandlingBuilder composition = builder.AddErrorHandling();
        composition.OnError((context, exception, cancellationToken) => throw new NotSupportedException("handler fault"));
        IHttpErrorHandlingFeature feature = builder.Features.OfType<IHttpErrorHandlingFeature>().Single();
        TestHttpContext context = new();

        // Act / Assert — secondary faults are not masked; the invoking boundary (behind which the
        // server's last-resort isolation stands) sees them.
        await Should.ThrowAsync<NotSupportedException>(
            async () => await feature.HandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - HandleAsync: Should reject null arguments")]
    public async Task HandleAsync_NullArguments_ShouldThrow()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        IHttpErrorHandlingFeature feature = Compose(builder);
        TestHttpContext context = new();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await feature.HandleAsync(null!, new InvalidOperationException("boom"), CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await feature.HandleAsync(context, null!, CancellationToken.None));
    }
}
