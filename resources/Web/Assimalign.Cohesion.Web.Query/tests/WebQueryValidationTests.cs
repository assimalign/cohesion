using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using CohesionMediaType = Assimalign.Cohesion.Http.HttpMediaType;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Query.Tests;

/// <summary>
/// RFC 10008 &#167; 2.1 / &#167; 2.3 compliance tests for <c>UseQueryValidation</c>, driven end to
/// end over the in-memory transport (real client, real HTTP/1.1 exchange, real dispatch): the
/// missing / malformed / unaccepted <c>Content-Type</c> refusals (400/415), the <c>Accept</c>
/// negotiation refusal (406), the <c>Accept-Query</c> advertisement, and the pass-through paths.
/// </summary>
public class WebQueryValidationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly NetHttpMethod QueryMethod = new("QUERY");

    private static async Task<(WebApplicationTestFactory Factory, HttpClient Client)> CreateEchoAppAsync(
        Action<WebQueryValidationOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var factory = new WebApplicationTestFactory();

        factory.Application.UseQueryValidation(configure);
        factory.Application.Use(async (context, next) =>
        {
            string body;
            using (var reader = new System.IO.StreamReader(context.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(context.RequestCancelled);
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            byte[] payload = Encoding.UTF8.GetBytes($"{context.Request.Method.Value}:{body}");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        await factory.StartAsync(cancellationToken);
        return (factory, factory.CreateClient());
    }

    private static HttpRequestMessage CreateQuery(string uri, HttpContent? content)
        => new(QueryMethod, uri) { Content = content };

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: QUERY content without a Content-Type is rejected 400 (RFC 10008 §2.3)")]
    public async Task UseQueryValidation_BodyWithoutContentType_ShouldRejectBadRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(cancellationToken: cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // ByteArrayContent carries no default Content-Type — the wire request declares none.
        var content = new ByteArrayContent("{\"q\":1}"u8.ToArray());
        content.Headers.ContentType.ShouldBeNull();

        // Act
        using HttpResponseMessage response = await client.SendAsync(CreateQuery("/search", content), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: the missing-Content-Type status is 415 when policy selects it")]
    public async Task UseQueryValidation_BodyWithoutContentTypeAndUnsupportedMediaTypePolicy_ShouldReject415()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.InvalidContentTypeStatusCode = CohesionHttpStatusCode.UnsupportedMediaType,
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("/search", new ByteArrayContent("{\"q\":1}"u8.ToArray())), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.UnsupportedMediaType);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: a malformed Content-Type is rejected 400 (RFC 10008 §2.1)")]
    public async Task UseQueryValidation_MalformedContentType_ShouldRejectBadRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(cancellationToken: cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        var content = new ByteArrayContent("{\"q\":1}"u8.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "not-a-media-type").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(CreateQuery("/search", content), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: a Content-Type outside the Accept-Query set is rejected 415 with the advertisement")]
    public async Task UseQueryValidation_ContentTypeOutsideAcceptedSet_ShouldReject415WithAcceptQuery()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.AcceptedMediaTypes.Add(CohesionMediaType.Parse("application/json")),
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("/search", new StringContent("name eq 'x'", Encoding.UTF8, "text/plain")), cancellation.Token);

        // Assert — 415, and the rejection advertises what the resource does accept (RFC 10008 §3).
        response.StatusCode.ShouldBe(NetHttpStatusCode.UnsupportedMediaType);
        response.Headers.TryGetValues("Accept-Query", out var acceptQuery).ShouldBeTrue();
        string.Join(",", acceptQuery!).ShouldContain("application/json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: an accepted Content-Type proceeds unchanged and the 200 advertises Accept-Query")]
    public async Task UseQueryValidation_AcceptedContentType_ShouldProceedUnchanged()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.AcceptedMediaTypes.Add(CohesionMediaType.Parse("application/json")),
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("/search", new StringContent("{\"q\":1}", Encoding.UTF8, "application/json")), cancellation.Token);

        // Assert — the query reached the terminal middleware with its content intact.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("QUERY:{\"q\":1}");
        response.Headers.TryGetValues("Accept-Query", out var acceptQuery).ShouldBeTrue();
        string.Join(",", acceptQuery!).ShouldContain("application/json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: an unsatisfiable Accept is rejected 406 (RFC 9110 §12.5.1)")]
    public async Task UseQueryValidation_UnsatisfiableAccept_ShouldReject406()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.SupportedResponseMediaTypes.Add(CohesionMediaType.Parse("application/json")),
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        HttpRequestMessage request = CreateQuery("/search", new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"));
        request.Headers.Accept.ParseAdd("text/csv");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotAcceptable);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: a missing Accept accepts every representation")]
    public async Task UseQueryValidation_MissingAccept_ShouldProceed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.SupportedResponseMediaTypes.Add(CohesionMediaType.Parse("application/json")),
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("/search", new StringContent("{\"q\":1}", Encoding.UTF8, "application/json")), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: a bodiless QUERY passes through")]
    public async Task UseQueryValidation_BodilessQuery_ShouldProceed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(cancellationToken: cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act — no content at all: nothing to type, nothing to reject.
        using HttpResponseMessage response = await client.SendAsync(CreateQuery("/search", content: null), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("QUERY:");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: non-QUERY requests pass through untouched")]
    public async Task UseQueryValidation_NonQueryMethod_ShouldPassThroughUntouched()
    {
        // Arrange — a POST with an untyped body would fail QUERY validation; it must not be touched.
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(
            options => options.AcceptedMediaTypes.Add(CohesionMediaType.Parse("application/json")),
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(NetHttpMethod.Post, "/search")
        {
            Content = new ByteArrayContent("a=1"u8.ToArray()),
        };

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — reached the terminal echo, and no Accept-Query advertisement was stamped.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("POST:a=1");
        response.Headers.TryGetValues("Accept-Query", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: a rejected exchange does not wedge the client's next request")]
    public async Task UseQueryValidation_RejectionWithUnreadBody_ShouldNotWedgeSubsequentRequest()
    {
        // Arrange — the 400 short-circuits without reading the request body; the transport owns
        // the unread remainder, and the client's next request must still complete.
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client) = await CreateEchoAppAsync(cancellationToken: cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        // Act
        using HttpResponseMessage rejected = await client.SendAsync(
            CreateQuery("/search", new ByteArrayContent(new byte[2048])), cancellation.Token);
        using HttpResponseMessage ok = await client.SendAsync(
            CreateQuery("/search", new StringContent("{\"q\":1}", Encoding.UTF8, "application/json")), cancellation.Token);

        // Assert
        rejected.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        ok.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Validation: InvalidContentTypeStatusCode rejects statuses other than 400/415")]
    public void InvalidContentTypeStatusCode_OutOfPolicy_ShouldThrow()
    {
        var options = new WebQueryValidationOptions();

        Should.Throw<ArgumentOutOfRangeException>(
            () => options.InvalidContentTypeStatusCode = CohesionHttpStatusCode.NotAcceptable);
    }
}
