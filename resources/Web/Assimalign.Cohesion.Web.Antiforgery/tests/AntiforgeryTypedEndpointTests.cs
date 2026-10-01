using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// Source-generated typed endpoints in an application that references Web.Antiforgery: the endpoint-binding
/// generator attaches the antiforgery requirement to every <c>[FromForm]</c>-bound endpoint, so form posts
/// are validated without a convention call, bind from the form the middleware parsed, opt out on their own
/// route, and fail closed when <c>UseAntiforgery</c> is missing. Endpoints that bind no form carry no
/// requirement.
/// </summary>
public class AntiforgeryTypedEndpointTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: A form-bound endpoint should reject a post without a token")]
    public async Task MapPost_FormBoundWithoutToken_ShouldAnswer400()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true);
        int invocations = 0;
        factory.Application.MapPost("/orders", async ([FromForm] string title, IHttpContext context) =>
        {
            invocations++;
            await WriteAsync(context, title);
        });

        using HttpClient client = factory.CreateClient();
        await FetchTokenAsync(client, cancellationToken);

        // Act
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("title", "widget")]);
        using HttpResponseMessage response = await client.PostAsync("/orders", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: A form-bound endpoint should bind its fields when the form carries a valid token")]
    public async Task MapPost_FormBoundWithFormToken_ShouldBindFromTheParsedForm()
    {
        // Arrange — the middleware reads the form for the token; the generated thunk binds from that parse.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true);
        factory.Application.MapPost("/orders", async ([FromForm] string title, [FromForm(Name = "qty")] int quantity, IHttpContext context) =>
        {
            await WriteAsync(context, $"{title}:{quantity}");
        });

        using HttpClient client = factory.CreateClient();
        string requestToken = await FetchTokenAsync(client, cancellationToken);

        // Act
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("title", "widget"),
            new KeyValuePair<string, string>("qty", "3"),
            new KeyValuePair<string, string>("__RequestVerificationToken", requestToken),
        ]);
        using HttpResponseMessage response = await client.PostAsync("/orders", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("widget:3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: A form-bound endpoint should accept a header token")]
    public async Task MapPost_FormBoundWithHeaderToken_ShouldBindFields()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true);
        factory.Application.MapPost("/orders", async ([FromForm] string title, IHttpContext context) =>
        {
            await WriteAsync(context, title);
        });

        using HttpClient client = factory.CreateClient();
        string requestToken = await FetchTokenAsync(client, cancellationToken);

        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Post, "/orders")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("title", "widget")]),
        };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("widget");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: A form-bound endpoint that disables antiforgery should run without a token or the middleware")]
    public async Task MapPost_FormBoundDisabled_ShouldRunWithoutTokenOrMiddleware()
    {
        // Arrange — the route's own opt-out follows the generated requirement, so it wins.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: false);
        factory.Application.MapPost("/webhook", async ([FromForm] string evt, IHttpContext context) =>
        {
            await WriteAsync(context, evt);
        }).DisableAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("evt", "paid")]);
        using HttpResponseMessage response = await client.PostAsync("/webhook", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("paid");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: Without UseAntiforgery a form-bound endpoint should fail at dispatch")]
    public async Task MapPost_FormBoundWithoutMiddleware_ShouldFailAtDispatch()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        InvalidOperationException? dispatchFailure = null;
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                dispatchFailure = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });
        factory.Application.UseRouting();

        int invocations = 0;
        factory.Application.MapPost("/orders", async ([FromForm] string title, IHttpContext context) =>
        {
            invocations++;
            await WriteAsync(context, title);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("title", "widget")]);
        using HttpResponseMessage response = await client.PostAsync("/orders", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        invocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseAntiforgery()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: A group-level opt-out should not reach a form-bound endpoint's own requirement")]
    public async Task MapPost_FormBoundInDisabledGroup_ShouldStillRequireToken()
    {
        // Arrange — the generated requirement is route-level metadata, more specific than the group's.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: true);
        factory.Application.MapGroup("/hooks").DisableAntiforgery()
            .MapPost("contact", async ([FromForm] string email, IHttpContext context) => await WriteAsync(context, email));

        using HttpClient client = factory.CreateClient();
        await FetchTokenAsync(client, cancellationToken);

        // Act
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("email", "ada@example.com")]);
        using HttpResponseMessage response = await client.PostAsync("/hooks/contact", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - Typed: An endpoint that binds no form should carry no antiforgery requirement")]
    public async Task MapPost_NotFormBound_ShouldRunWithoutTokenOrMiddleware()
    {
        // Arrange — query binding on a POST, no UseAntiforgery: nothing requires it, so nothing fails.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory(useAntiforgery: false);
        factory.Application.MapPost("/search", async (string q, IHttpContext context) => await WriteAsync(context, q));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/search?q=cohesion", new StringContent(string.Empty, Encoding.UTF8), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("cohesion");
    }

    private static WebApplicationTestFactory CreateFactory(bool useAntiforgery)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddAntiforgery();
        factory.Application.UseRouting();

        if (useAntiforgery)
        {
            factory.Application.UseAntiforgery();
        }

        MapTokenEndpoint(factory.Application);
        return factory;
    }

    private static void MapTokenEndpoint(WebApplication application)
        => application.MapGet("/token", async context =>
        {
            HttpAntiforgeryTokenSet tokens = context.RequireAntiforgery.GetAndStoreTokens(context);
            await WriteAsync(context, tokens.RequestToken!);
        });

    private static async Task<string> FetchTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync("/token", cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task WriteAsync(IHttpContext context, string text)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestCancelled);
    }
}
