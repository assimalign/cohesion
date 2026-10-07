using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.StaticFiles;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/>: a rewrite ahead of the real router
/// selects the other endpoint and names the server span by its route while the span keeps the client's path,
/// static files serve the rewritten path (at the application level and in a path branch), a rewritten query
/// reaches the source-generated query binding, a redirect is followed with its method kept, the endpoint
/// reads the client's URL from <see cref="IWebRewriteFeature"/>, and a target no request can carry is a 400.
/// </summary>
public class RewriteEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: A rewrite ahead of UseRouting should select the rewritten path's endpoint")]
    public async Task UseRewrite_ThenUseRouting_ShouldSelectRewrittenEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int oldInvocations = 0;

        factory.Application.UseRewrite(rules => rules.AddRewrite("^/old/(\\d+)$", "/new/$1"));
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/old/{id:int}", Text(_ => "old", () => Interlocked.Increment(ref oldInvocations)));
        routes.Map(CohesionHttpMethod.Get, "/new/{id:int}", Text(context =>
            context.TryGetRouteValues(out RouteValueDictionary? values) ? $"new:{values!["id"]}" : "new:none"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/old/5", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("new:5");
        oldInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: The server span should keep the client's path and carry the rewritten route")]
    public async Task UseRewrite_ThenUseRouting_ShouldReportOriginalPathAndRewrittenRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        TaskCompletionSource<Activity> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "Assimalign.Cohesion.Web.Hosting",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("url.path"), "/legacy/telemetry/9"))
                {
                    stopped.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRewrite(rules => rules.AddRewrite("^/legacy/telemetry/(\\d+)$", "/telemetry/orders/$1"));
        factory.Application.UseRouting().Map(CohesionHttpMethod.Get, "/telemetry/orders/{id:int}", Text(_ => "order"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/legacy/telemetry/9", cancellation.Token);
        Activity span = await stopped.Task.WaitAsync(cancellation.Token);

        // Assert — telemetry ran before the rewrite: the path is the client's, the route the rewritten path's.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        span.GetTagItem("url.path").ShouldBe("/legacy/telemetry/9");
        span.GetTagItem("http.route").ShouldBe("/telemetry/orders/{id:int}");
        span.DisplayName.ShouldBe("GET /telemetry/orders/{id:int}");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: Static files should serve the rewritten path")]
    public async Task UseRewrite_ThenUseStaticFiles_ShouldServeRewrittenFile()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "console.log('app');"));
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRewrite(rules => rules.AddRewrite("^/assets/v\\d+/(.*)$", "/$1"));
        factory.Application.UseStaticFiles(site);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage rewritten = await client.GetAsync("/assets/v3/app.js", cancellation.Token);
        using HttpResponseMessage unversioned = await client.GetAsync("/assets/app.js", cancellation.Token);

        // Assert
        rewritten.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await rewritten.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("console.log('app');");
        unversioned.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: Static files in a branch should serve the branch's rewritten path")]
    public async Task UseRewrite_InPathBranchWithStaticFiles_ShouldServeRewrittenFile()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using InMemoryFileSystem site = StaticSite.Create(("app.js", "console.log('app');"));
        await using WebApplicationTestFactory factory = new();
        factory.Application.Map("/static", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/v\\d+/(.*)$", "/$1"));
            branch.UseStaticFiles(site);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/static/v2/app.js", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("console.log('app');");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: A branch should see its path base, the rewritten path below it, and the joined request path")]
    public async Task UseRewrite_InPathBranch_ShouldReportBaseEffectiveAndRequestPath()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/latest/(.*)$", "/v2/$1"));
            branch.Run(context => WriteTextAsync(context, $"{context.GetPathBase()}|{context.GetEffectivePath()}|{context.Request.Path}"));
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/docs/latest/intro", cancellation.Token);

        // Assert
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("/docs|/v2/intro|/docs/v2/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: A rewritten query should reach the generated query binding of a typed endpoint")]
    public async Task UseRewrite_QueryRewrite_ShouldReachTypedEndpointBinding()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRewrite(rules => rules.AddRewrite("^/products/(\\d+)$", "/product?id=$1&view=full"));
        factory.Application.UseRouting();
        factory.Application.MapGet("/product", (int id, string view) => $"product:{id}:{view}");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/products/7?view=summary", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("product:7:full");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: A 308 redirect should be followed with the request method kept")]
    public async Task UseRewrite_PermanentRedirect_ShouldBeFollowedWithMethodKept()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        int oldInvocations = 0;

        factory.Application.UseRewrite(rules => rules.AddRedirect("^/v1/orders$", "/v2/orders", CohesionHttpStatusCode.PermanentRedirect));
        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Post, "/v1/orders", Text(_ => "v1", () => Interlocked.Increment(ref oldInvocations)));
        routes.Map(CohesionHttpMethod.Post, "/v2/orders", Text(context => $"{context.Request.Method.Value} v2"));

        using HttpClient client = factory.CreateClient();
        using StringContent body = new("{}", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/v1/orders?src=app", body, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.PathAndQuery.ShouldBe("/v2/orders?src=app");
        response.RequestMessage.Method.ShouldBe(NetHttpMethod.Post);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("POST v2");
        oldInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: The endpoint should read the client's URL from the rewrite feature")]
    public async Task UseRewrite_EndpointReadsFeature_ShouldSeeClientUrl()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRewrite(rules => rules.AddRewrite("^/shop/(.*)$", "/store/$1?ref=shop"));
        factory.Application.UseRouting().Map(CohesionHttpMethod.Get, "/store/{**rest}", Text(context =>
        {
            IWebRewriteFeature feature = context.Features.Get<IWebRewriteFeature>()!;
            return $"{feature.OriginalPath}?{feature.OriginalQuery["page"]}|{context.Request.Path}?{context.Request.Query["ref"]}";
        }));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/shop/shoes?page=3", cancellation.Token);

        // Assert
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("/shop/shoes?3|/store/shoes?shop");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - E2E: A target that decodes to a path no request can carry should answer 400")]
    public async Task UseRewrite_TargetDecodesToInvalidPath_ShouldAnswerBadRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseRewrite(rules => rules.AddRewrite(
            "^/search\\?q=([^&]+)$",
            "/find/$1?",
            RewriteFlow.Continue,
            RewriteMatchTarget.PathAndQuery));
        factory.Application.Run(context => WriteTextAsync(context, context.Request.Path.Value));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage invalid = await client.GetAsync("/search?q=a%20b", cancellation.Token);
        using HttpResponseMessage valid = await client.GetAsync("/search?q=caf%C3%A9", cancellation.Token);

        // Assert
        invalid.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        valid.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await valid.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("/find/café");
    }

    private static RouterRouteHandler Text(Func<IHttpContext, string> text, Action? onInvoke = null) => new(context =>
    {
        onInvoke?.Invoke();
        return WriteTextAsync(context, text(context));
    });

    private static async Task WriteTextAsync(IHttpContext context, string text)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestCancelled);
    }
}
