using System;
using System.Collections.Generic;
using System.Linq;
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

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> (in-memory HTTP/1.1) with the
/// real router: a handler mints a token pair through the exchange's antiforgery service, and the client
/// returns it in the cookie-and-header flow or the form-token flow. The client's cookie container carries
/// the cookie token, as a browser does. Also covers the 400 problem+json rejection, safe methods, a CORS
/// preflight, the fail-closed dispatch when the middleware is missing or misordered, a missing
/// registration failing startup, a header token leaving a multipart body to the endpoint, and an exchange
/// that replaces the registered service.
/// </summary>
public class AntiforgeryEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A cookie and header token pair should pass validation")]
    public async Task UseAntiforgery_CookieAndHeaderTokens_ShouldRunEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        int endpointInvocations = 0;
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", Respond("accepted", () => endpointInvocations++)).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage tokenResponse = await client.GetAsync("/token", cancellationToken);
        string requestToken = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

        using HttpRequestMessage request = new(NetHttpMethod.Post, "/submit");
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken).ShouldBeTrue();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        tokenResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).ShouldBeTrue();
        cookies!.ShouldContain(cookie => cookie.StartsWith("__cohesion-antiforgery=", StringComparison.Ordinal));
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("accepted");
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A form token should pass validation and leave the parsed form to the endpoint")]
    public async Task UseAntiforgery_FormToken_ShouldRunEndpointWithParsedForm()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", async context =>
        {
            string name = context.Request.Form.TryGetValue("name", out HttpQueryValue value) ? value.Value : "(missing)";
            await WriteAsync(context, name);
        }).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();
        string requestToken = await FetchTokenAsync(client, cancellationToken);

        // Act
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("name", "ada"),
            new KeyValuePair<string, string>("__RequestVerificationToken", requestToken),
        ]);
        using HttpResponseMessage response = await client.PostAsync("/submit", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("ada");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A request without a token should get 400 problem+json and not reach the endpoint")]
    public async Task UseAntiforgery_MissingToken_ShouldAnswer400ProblemDetails()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        int endpointInvocations = 0;
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", Respond("accepted", () => endpointInvocations++)).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();
        await FetchTokenAsync(client, cancellationToken);

        // Act — the cookie token is present, the request token is not.
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("name", "ada")]);
        using HttpResponseMessage response = await client.PostAsync("/submit", form, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.ShouldContain("\"status\":400", Case.Sensitive);
        endpointInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A request token without its cookie should be rejected")]
    public async Task UseAntiforgery_TokenWithoutCookie_ShouldAnswer400()
    {
        // Arrange — the token is replayed from a client that never received the cookie, as a cross-site
        // page that obtained a token would.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", Respond("accepted")).RequireAntiforgery();

        using HttpClient victim = factory.CreateClient();
        string requestToken = await FetchTokenAsync(victim, cancellationToken);

        using HttpClient attacker = factory.CreateClient();
        using HttpRequestMessage request = new(NetHttpMethod.Post, "/submit");
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await attacker.SendAsync(request, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A safe method should reach a protected endpoint without a token")]
    public async Task UseAntiforgery_SafeMethod_ShouldRunEndpointWithoutToken()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapGet("/page", Respond("page")).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/page", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A CORS preflight should not be validated against its candidate endpoint")]
    public async Task UseAntiforgery_CorsPreflight_ShouldPassUnvalidated()
    {
        // Arrange — the POST route requires antiforgery; nothing answers the preflight.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        int endpointInvocations = 0;
        factory.Application.Map(CohesionHttpMethod.Post, "/items/{id:int}", Respond("accepted", () => endpointInvocations++)).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage preflight = new(NetHttpMethod.Options, "/items/7");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://app.example").ShouldBeTrue();
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(preflight, cancellationToken);

        // Assert — the unanswered preflight is the plain OPTIONS request it is: neither a 400 nor a
        // dispatch failure, and the candidate never runs.
        response.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        endpointInvocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: Without UseAntiforgery a protected endpoint should fail at dispatch instead of running unprotected")]
    public async Task UseAntiforgery_Missing_ShouldFailEndpointAtDispatch()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        InvalidOperationException? dispatchFailure = null;
        ObserveDispatchFailure(factory.Application, exception => dispatchFailure = exception);
        factory.Application.UseRouting();

        int endpointInvocations = 0;
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", Respond("accepted", () => endpointInvocations++)).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/submit", new StringContent(string.Empty), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        endpointInvocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseAntiforgery()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: Registered ahead of UseRouting, a protected endpoint should fail at dispatch")]
    public async Task UseAntiforgery_RegisteredBeforeRouting_ShouldFailEndpointAtDispatch()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAntiforgery();

        InvalidOperationException? dispatchFailure = null;
        ObserveDispatchFailure(factory.Application, exception => dispatchFailure = exception);
        factory.Application.UseAntiforgery();
        factory.Application.UseRouting();

        int endpointInvocations = 0;
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", Respond("accepted", () => endpointInvocations++)).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/submit", new StringContent(string.Empty), cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        endpointInvocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseAntiforgery()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: UseAntiforgery without AddAntiforgery should fail the application start")]
    public async Task UseAntiforgery_WithoutRegistration_ShouldFailStart()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting();
        factory.Application.UseAntiforgery();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => factory.StartAsync(cancellationToken));

        // Assert
        exception.Message.ShouldContain("AddAntiforgery()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: A header token should leave a multipart body unread for the endpoint")]
    public async Task UseAntiforgery_HeaderTokenWithMultipartBody_ShouldLeaveBodyToEndpoint()
    {
        // Arrange — the endpoint streams its body itself, as an upload endpoint would.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.Map(CohesionHttpMethod.Post, "/upload", async context =>
        {
            bool formParsed = context.Features.Get<IHttpFormFeature>() is not null;
            using System.IO.MemoryStream buffer = new();
            await context.Request.Body.CopyToAsync(buffer, context.RequestCancelled);
            await WriteAsync(context, $"{formParsed}:{buffer.Length}");
        }).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();
        string requestToken = await FetchTokenAsync(client, cancellationToken);

        using MultipartFormDataContent content = new("upload-boundary");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 4096))), "file", "data.bin");
        byte[] sent = await content.ReadAsByteArrayAsync(cancellationToken);

        using HttpRequestMessage request = new(NetHttpMethod.Post, "/upload") { Content = content };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe($"False:{sent.Length}");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - E2E: Replacing the exchange's service should replace the registered one and validate with it")]
    public async Task UseAntiforgery_ExchangeServiceReplaced_ShouldValidateWithReplacement()
    {
        // Arrange — a middleware ahead of routing swaps in its own service for every exchange, so handlers
        // mint with it; one antiforgery feature remains on the exchange and validation uses it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        IHttpAntiforgery replacement = HttpAntiforgery.Create();

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAntiforgery();
        factory.Application.Use((context, next) =>
        {
            context.Antiforgery = replacement;
            return next.Invoke(context);
        });
        factory.Application.UseRouting();
        factory.Application.UseAntiforgery();
        MapTokenEndpoint(factory.Application);
        factory.Application.Map(CohesionHttpMethod.Post, "/submit", async context =>
        {
            int features = context.Features.OfType<IHttpAntiforgeryFeature>().Count();
            await WriteAsync(context, $"{features}:{ReferenceEquals(context.Antiforgery, replacement)}");
        }).RequireAntiforgery();

        using HttpClient client = factory.CreateClient();
        string requestToken = await FetchTokenAsync(client, cancellationToken);

        using HttpRequestMessage request = new(NetHttpMethod.Post, "/submit");
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("1:True");
    }

    // A routed application with antiforgery registered and validated, plus the token-minting endpoint.
    private static WebApplicationTestFactory CreateFactory()
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAntiforgery();
        factory.Application.UseRouting();
        factory.Application.UseAntiforgery();
        MapTokenEndpoint(factory.Application);
        return factory;
    }

    // The render path: mint the pair through the exchange's service, store the cookie token, and hand the
    // request token to the client (a page would embed it in a hidden field instead).
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

    // Observes a dispatch failure the way an exception boundary would.
    private static void ObserveDispatchFailure(WebApplication application, Action<InvalidOperationException> observe)
        => application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                observe(exception);
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });

    private static WebApplicationMiddleware Respond(string text, Action? onInvoke = null) => async context =>
    {
        onInvoke?.Invoke();
        await WriteAsync(context, text);
    };

    private static async Task WriteAsync(IHttpContext context, string text)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestCancelled);
    }
}
