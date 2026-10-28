using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>
/// Coverage for the status-code-pages middleware (<c>UseStatusCodePages</c>): it upgrades a bodyless
/// <c>4xx</c>/<c>5xx</c> terminal response into a body (RFC 9457 problem+json by default, or a custom
/// responder) and leaves every other response — success codes, responses that already carry a body,
/// and started responses — untouched.
/// </summary>
public class StatusCodePagesMiddlewareTests
{
    /// <summary>
    /// The middleware <c>builder.Services.AddStatusCodePages(pages =&gt; ...)</c> registers.
    /// </summary>
    private static IWebApplicationMiddleware Registration(Action<StatusCodePagesOptions>? configure)
    {
        StatusCodePagesBuilder pages = new();
        configure?.Invoke(pages.Options);
        return pages.Build();
    }

    private static async Task<TestHttpContext> RunAsync(
        Action<StatusCodePagesOptions>? configure,
        Func<IHttpContext, Task> downstream,
        IHttpResponseStreamingFeature? streaming = null)
    {
        TestPipelineBuilder builder = new();
        builder.RegisteredMiddleware.Add(Registration(configure));
        builder.UseStatusCodePages();
        builder.Run(downstream);
        IWebApplicationPipeline pipeline = builder.Build();

        TestHttpContext context = new();
        if (streaming is not null)
        {
            context.Features.Set(streaming);
        }

        await pipeline.ExecuteAsync(context);
        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - StatusCodePages: Should render a bodyless 404 as problem+json")]
    public async Task InvokeAsync_BodylessError_ShouldWriteProblemJson()
    {
        // Act
        TestHttpContext context = await RunAsync(null, ctx =>
        {
            ctx.Response.StatusCode = (HttpStatusCode)404;
            return Task.CompletedTask;
        });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - StatusCodePages: Should not touch a response that already wrote a body")]
    public async Task InvokeAsync_ResponseWithBody_ShouldNotTouch()
    {
        // Act
        TestHttpContext context = await RunAsync(null, ctx =>
        {
            ctx.Response.StatusCode = (HttpStatusCode)404;
            ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
            ctx.Response.Body.Write("custom not found"u8);
            return Task.CompletedTask;
        });

        // Assert
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("text/plain");
        context.ResponseBodyText().ShouldBe("custom not found");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - StatusCodePages: Should not touch a success response")]
    public async Task InvokeAsync_SuccessStatus_ShouldNotTouch()
    {
        // Act
        TestHttpContext context = await RunAsync(null, ctx =>
        {
            ctx.Response.StatusCode = (HttpStatusCode)204;
            return Task.CompletedTask;
        });

        // Assert
        context.Response.StatusCode.ShouldBe((HttpStatusCode)204);
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - StatusCodePages: Should invoke a custom responder for a bodyless error")]
    public async Task InvokeAsync_CustomResponder_ShouldInvoke()
    {
        // Act
        TestHttpContext context = await RunAsync(
            options => options.Responder = async ctx =>
            {
                ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
                await ctx.Response.Body.WriteAsync("responder body"u8.ToArray());
            },
            ctx =>
            {
                ctx.Response.StatusCode = (HttpStatusCode)500;
                return Task.CompletedTask;
            });

        // Assert
        context.ResponseBodyText().ShouldBe("responder body");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - StatusCodePages: Should not touch a bodyless error whose head already committed")]
    public async Task InvokeAsync_StartedResponse_ShouldNotTouch()
    {
        // Act
        TestHttpContext context = await RunAsync(
            null,
            ctx =>
            {
                ctx.Response.StatusCode = (HttpStatusCode)503;
                return Task.CompletedTask;
            },
            streaming: new FakeResponseStreamingFeature(hasStarted: true));

        // Assert
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseStatusCodePages: Should reject a null pipeline builder")]
    public void UseStatusCodePages_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseStatusCodePages());
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseStatusCodePages: Should place the middleware the application registered")]
    public async Task UseStatusCodePages_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.RegisteredMiddleware.Add(Registration(options => options.Responder = WriteAsync("registered")));
        builder.UseStatusCodePages();
        builder.Run(NotFoundAsync);
        TestHttpContext context = new();

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("registered");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseStatusCodePages: The last registration should win")]
    public async Task UseStatusCodePages_TwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.RegisteredMiddleware.Add(Registration(options => options.Responder = WriteAsync("first")));
        builder.RegisteredMiddleware.Add(Registration(options => options.Responder = WriteAsync("last")));
        builder.UseStatusCodePages();
        builder.Run(NotFoundAsync);
        TestHttpContext context = new();

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("last");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseStatusCodePages: Without a registration it should place the problem+json default")]
    public async Task UseStatusCodePages_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseStatusCodePages();
        builder.Run(NotFoundAsync);
        TestHttpContext context = new();

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - AddStatusCodePages: A responder set after registration should not reach the middleware")]
    public async Task UseStatusCodePages_ResponderSetAfterRegistration_ShouldKeepTheRegisteredOptions()
    {
        // Arrange — Build copies the options, as the generated verb calls it when it registers.
        StatusCodePagesBuilder pages = new();
        IWebApplicationMiddleware registered = pages.Build();
        pages.Options.Responder = WriteAsync("late");
        TestPipelineBuilder builder = new();
        builder.RegisteredMiddleware.Add(registered);
        builder.UseStatusCodePages();
        builder.Run(NotFoundAsync);
        TestHttpContext context = new();

        // Act
        await builder.Build().ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldNotContain("late", Case.Sensitive);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
    }

    private static Task NotFoundAsync(IHttpContext context)
    {
        context.Response.StatusCode = HttpStatusCode.NotFound;
        return Task.CompletedTask;
    }

    private static Func<IHttpContext, Task> WriteAsync(string body) => async context =>
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
    };
}
