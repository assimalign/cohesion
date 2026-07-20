using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Api.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for the source-generated typed-delegate endpoint binding: real requests are
/// driven through <see cref="WebApplicationTestFactory"/> and the generated thunks bind each source
/// and enforce the failure semantics.
/// </summary>
public class EndpointBindingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static async Task WriteTextAsync(IHttpContext context, string text)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestCancelled);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: route value binds to a typed parameter")]
    public async Task Binding_RouteValue_ShouldBindTypedParameter()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/widgets/{id}", async (int id, IHttpContext context) =>
        {
            await WriteTextAsync(context, $"widget:{id}");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/widgets/42", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("widget:42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: typed route constraint carries a boxed value")]
    public async Task Binding_TypedRouteConstraint_ShouldBindBoxedValue()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/typed/{id:int}", async (int id, IHttpContext context) =>
        {
            await WriteTextAsync(context, $"typed:{id}");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/typed/7", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("typed:7");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: query scalars bind by inference")]
    public async Task Binding_QueryScalars_ShouldBindByInference()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/search", async (string q, int page, IHttpContext context) =>
        {
            await WriteTextAsync(context, $"{q}:{page}");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/search?q=hello&page=2", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("hello:2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: missing required query yields 400 problem")]
    public async Task Binding_MissingRequiredQuery_ShouldReturnBadRequest()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/needs", async (string q, IHttpContext context) =>
        {
            await WriteTextAsync(context, q);
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/needs", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);
        body.ShouldContain("\"errors\"", Case.Sensitive);
        body.ShouldContain("\"q\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: unparseable query scalar yields 400 problem")]
    public async Task Binding_UnparseableQuery_ShouldReturnBadRequest()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/paged", async (int page, IHttpContext context) =>
        {
            await WriteTextAsync(context, page.ToString());
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/paged?page=notanumber", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("\"page\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: nullable query is optional")]
    public async Task Binding_NullableQuery_ShouldBeOptional()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/optional", async (int? limit, IHttpContext context) =>
        {
            await WriteTextAsync(context, limit?.ToString() ?? "none");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage absent = await client.GetAsync("/optional", cancellation.Token);
        absent.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await absent.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("none");

        using HttpResponseMessage present = await client.GetAsync("/optional?limit=9", cancellation.Token);
        (await present.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("9");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: header binds by explicit attribute")]
    public async Task Binding_Header_ShouldBindByAttribute()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/whoami", async ([FromHeader(Name = "X-User")] string user, IHttpContext context) =>
        {
            await WriteTextAsync(context, user);
        });

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "alice");

        using HttpResponseMessage response = await client.GetAsync("/whoami", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: JSON body binds through the serialization registry")]
    public async Task Binding_JsonBody_ShouldBindThroughRegistry()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ApiTestJsonContext.Default);

        factory.Application.UseRouting();

        factory.Application.MapPost("/widgets", async (Widget widget, IHttpContext context) =>
        {
            await WriteTextAsync(context, $"{widget.Name}:{widget.Quantity}");
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("""{"name":"gizmo","quantity":3}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync("/widgets", content, cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("gizmo:3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: unsupported body content type yields 415")]
    public async Task Binding_UnsupportedBodyContentType_ShouldReturnUnsupportedMediaType()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ApiTestJsonContext.Default);

        factory.Application.UseRouting();

        factory.Application.MapPost("/widgets", async (Widget widget, IHttpContext context) =>
        {
            await WriteTextAsync(context, widget.Name);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("gizmo", Encoding.UTF8, "text/plain");

        using HttpResponseMessage response = await client.PostAsync("/widgets", content, cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.UnsupportedMediaType);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: malformed JSON body yields 400")]
    public async Task Binding_MalformedJsonBody_ShouldReturnBadRequest()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(ApiTestJsonContext.Default);

        factory.Application.UseRouting();

        factory.Application.MapPost("/widgets", async (Widget widget, IHttpContext context) =>
        {
            await WriteTextAsync(context, widget.Name);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("""{"name":"gizmo", "quantity":""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync("/widgets", content, cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: form fields bind by attribute")]
    public async Task Binding_FormFields_ShouldBindByAttribute()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapPost("/upload", async ([FromForm] string title, [FromForm(Name = "qty")] int quantity, IHttpContext context) =>
        {
            await WriteTextAsync(context, $"{title}:{quantity}");
        });

        using HttpClient client = factory.CreateClient();
        using FormUrlEncodedContent content = new(new[]
        {
            new KeyValuePair<string, string>("title", "boxes"),
            new KeyValuePair<string, string>("qty", "5")
        });

        using HttpResponseMessage response = await client.PostAsync("/upload", content, cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("boxes:5");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: CancellationToken and IHttpContext inject directly")]
    public async Task Binding_Injections_ShouldBindContextAndToken()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/inject", async (IHttpContext context, CancellationToken token) =>
        {
            await WriteTextAsync(context, token.CanBeCanceled ? "cancellable" : "none");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/inject", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("cancellable");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Binding: single-context handler uses the middleware overload")]
    public async Task Binding_SingleContextHandler_ShouldUseMiddlewareOverload()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRouting();

        factory.Application.MapGet("/raw", async (IHttpContext context) =>
        {
            await WriteTextAsync(context, "raw");
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/raw", cancellation.Token);

        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("raw");
    }
}
