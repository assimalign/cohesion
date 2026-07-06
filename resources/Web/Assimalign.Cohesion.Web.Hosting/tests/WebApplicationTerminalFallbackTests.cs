using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the pipeline terminal that replaced the silent <c>Task.CompletedTask</c> in
/// <c>WebApplication.Build</c> (issue #776): an unhandled request now yields a 404 problem+json
/// instead of an empty response, while a response a middleware already shaped is left untouched.
/// </summary>
public class WebApplicationTerminalFallbackTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: an unhandled request yields a 404 problem+json")]
    public async Task Terminal_WhenNoMiddlewareHandles_Writes404ProblemJson()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();
        TerminalTestContext context = new();

        await pipeline.ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(404);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.BodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: a status a middleware already chose is not overwritten")]
    public async Task Terminal_WhenMiddlewareChoseStatus_DoesNotOverwrite()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();
        app.Use((context, next) =>
        {
            context.Response.StatusCode = HttpStatusCode.NoContent;
            return next.Invoke(context);
        });
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();
        TerminalTestContext context = new();

        await pipeline.ExecuteAsync(context);

        context.Response.StatusCode.Value.ShouldBe(204);
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
        context.BodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: a body a middleware already wrote is not overwritten")]
    public async Task Terminal_WhenMiddlewareWroteBody_DoesNotOverwrite()
    {
        WebApplication app = WebApplication.CreateBuilder().Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("hello"));
            await next.Invoke(context);
        });
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();
        TerminalTestContext context = new();

        await pipeline.ExecuteAsync(context);

        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/plain");
        context.BodyText().ShouldBe("hello");
    }

    private sealed class TerminalTestContext : IHttpContext
    {
        public TerminalTestContext()
        {
            Request = new TerminalTestRequest(this);
            Response = new TerminalTestResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;

        public void Cancel()
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string BodyText() => Encoding.UTF8.GetString(((MemoryStream)Response.Body).ToArray());
    }

    private sealed class TerminalTestRequest : IHttpRequest
    {
        public TerminalTestRequest(IHttpContext context) => HttpContext = context;

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => new("/missing");
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body => Stream.Null;
    }

    private sealed class TerminalTestResponse : IHttpResponse
    {
        public TerminalTestResponse(IHttpContext context) => HttpContext = context;

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = new MemoryStream();
    }
}
