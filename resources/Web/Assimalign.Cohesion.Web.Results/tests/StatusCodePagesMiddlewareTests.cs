using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Verifies the status-code-pages middleware registered by <c>UseStatusCodePages()</c>: it fills a
/// bodyless 4xx/5xx terminal response with problem+json, leaves responses that already have a body
/// (or a success status) untouched, and defers to a custom responder when configured.
/// </summary>
public class StatusCodePagesMiddlewareTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseStatusCodePages: fills a bodyless 404 with problem+json")]
    public async Task Invoke_WhenBodyless404_WritesProblemJson()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseStatusCodePages();
        builder.Use((ctx, next) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.NotFound;
            return next.Invoke(ctx);
        });

        await builder.Build().ExecuteAsync(context);

        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        JsonElement root = document.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(404);
        root.GetProperty("title").GetString().ShouldBe("Not Found");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseStatusCodePages: leaves a 4xx that already has a body untouched")]
    public async Task Invoke_WhenErrorResponseHasBody_LeavesItUntouched()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseStatusCodePages();
        builder.Use(async (ctx, _) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.BadRequest;
            ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("custom error body"));
        });

        await builder.Build().ExecuteAsync(context);

        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/plain");
        context.ResponseBodyText().ShouldBe("custom error body");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseStatusCodePages: leaves a success response untouched")]
    public async Task Invoke_WhenSuccessStatus_LeavesItUntouched()
    {
        TestHttpContext context = new();
        TestPipelineBuilder builder = new();
        builder.UseStatusCodePages();
        builder.Use((ctx, next) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.NoContent;
            return next.Invoke(ctx);
        });

        await builder.Build().ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(204);
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - UseStatusCodePages: defers to a custom responder")]
    public async Task Invoke_WithCustomResponder_InvokesResponder()
    {
        TestHttpContext context = new();
        bool responderRan = false;

        TestPipelineBuilder builder = new();
        builder.UseStatusCodePages(options => options.Responder = ctx =>
        {
            responderRan = true;
            ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/html";
            return Task.CompletedTask;
        });
        builder.Use((ctx, next) =>
        {
            ctx.Response.StatusCode = HttpStatusCode.NotFound;
            return next.Invoke(ctx);
        });

        await builder.Build().ExecuteAsync(context);

        responderRan.ShouldBeTrue();
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/html");
    }
}
