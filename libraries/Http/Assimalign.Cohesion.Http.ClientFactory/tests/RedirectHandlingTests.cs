using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ClientFactory.Tests;

// The BCL pipeline types — the enclosing Assimalign.Cohesion.Http namespace would otherwise
// shadow them with the Cohesion protocol value objects.
using HttpMethod = System.Net.Http.HttpMethod;
using HttpStatusCode = System.Net.HttpStatusCode;

/// <summary>
/// RFC 10008 &#167; 2.5 / RFC 9110 &#167; 15.4 compliance tests for the factory-owned automatic
/// redirect layer: QUERY is re-issued (never rewritten to GET) with its original content on
/// <c>301</c>/<c>302</c>/<c>307</c>/<c>308</c>, <c>303 See Other</c> is fulfilled with a GET,
/// the legacy POST rewrite is preserved, and the safety rules (credential stripping, downgrade
/// refusal, hop cap) hold.
/// </summary>
public class RedirectHandlingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpMethod QueryMethod = new("QUERY");

    private static HttpClient CreateClient(
        ScriptedRedirectHandler handler,
        Action<NamedHttpClientOptions>? configure = null)
    {
        IHttpClientFactory factory = new HttpClientFactoryBuilder()
            .AddClient("api", options =>
            {
                options.HandlerFactory = () => handler;
                configure?.Invoke(options);
            })
            .Build();

        return factory.Create("api");
    }

    private static HttpRequestMessage CreateQuery(string uri, string content = "{\"q\":\"cohesion\"}")
        => new(QueryMethod, uri)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
        };

    [Theory(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: QUERY is re-issued with its content on 301/302/307/308 (RFC 10008 §2.5)")]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.RedirectKeepVerb)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task SendAsync_QueryRedirected_ShouldReissueQueryWithContent(HttpStatusCode statusCode)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(statusCode, "https://origin.example/search-v2")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert — the follow-up hop is a QUERY carrying the original content to the Location URI.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe("QUERY");
        handler.Requests[1].Uri.ShouldBe(new Uri("https://origin.example/search-v2"));
        handler.Requests[1].Content.ShouldBe("{\"q\":\"cohesion\"}");
        handler.Requests[1].ContentType.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: 303 See Other switches QUERY to a GET without content (RFC 10008 §2.5.3)")]
    public async Task SendAsync_QueryRedirectedWith303_ShouldFollowWithGetWithoutContent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.SeeOther, "https://origin.example/results/42")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert — the one sanctioned method switch: GET on the Location URI, no content.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe("GET");
        handler.Requests[1].Uri.ShouldBe(new Uri("https://origin.example/results/42"));
        handler.Requests[1].Content.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: the legacy POST rewrite to GET on 301/302 is preserved")]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    public async Task SendAsync_PostRedirected_ShouldRewriteToGet(HttpStatusCode statusCode)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(statusCode, "https://origin.example/moved")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://origin.example/form")
        {
            Content = new StringContent("a=1"),
        };

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — RFC 9110 §15.4.2/§15.4.3: the historical rewrite applies to POST alone.
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe("GET");
        handler.Requests[1].Content.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: 307 preserves POST with content")]
    public async Task SendAsync_PostRedirectedWith307_ShouldPreservePost()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.RedirectKeepVerb, "https://origin.example/retry")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://origin.example/form")
        {
            Content = new StringContent("a=1"),
        };

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe("POST");
        handler.Requests[1].Content.ShouldBe("a=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: a relative Location resolves against the request URI")]
    public async Task SendAsync_RelativeLocation_ShouldResolveAgainstRequestUri()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.RedirectKeepVerb, "/search-v2?page=1")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/api/search"), cancellation.Token);

        // Assert
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Uri.ShouldBe(new Uri("https://origin.example/search-v2?page=1"));
        handler.Requests[1].Method.ShouldBe("QUERY");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: an https to http downgrade is not followed")]
    public async Task SendAsync_HttpsToHttpRedirect_ShouldNotFollow()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.RedirectKeepVerb, "http://origin.example/insecure");
        using HttpClient client = CreateClient(handler);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert — the raw redirect surfaces; only the original request was sent.
        response.StatusCode.ShouldBe(HttpStatusCode.RedirectKeepVerb);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: the Authorization field is dropped on the redirected hop")]
    public async Task SendAsync_RedirectWithAuthorization_ShouldDropCredential()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.RedirectKeepVerb, "https://elsewhere.example/search")
            .EnqueueOk();
        using HttpClient client = CreateClient(handler);
        HttpRequestMessage request = CreateQuery("https://origin.example/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].HasAuthorization.ShouldBeTrue();
        handler.Requests[1].HasAuthorization.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: exceeding MaxAutomaticRedirections returns the last redirect")]
    public async Task SendAsync_RedirectChainPastCap_ShouldReturnLastRedirect()
    {
        // Arrange — three redirects with a cap of two: the third 3xx surfaces to the caller.
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.Moved, "https://origin.example/1")
            .EnqueueRedirect(HttpStatusCode.Moved, "https://origin.example/2")
            .EnqueueRedirect(HttpStatusCode.Moved, "https://origin.example/3");
        using HttpClient client = CreateClient(handler, options => options.MaxAutomaticRedirections = 2);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Moved);
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: AllowAutoRedirect=false surfaces the raw 3xx")]
    public async Task SendAsync_AutoRedirectDisabled_ShouldReturnRawRedirect()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.PermanentRedirect, "https://origin.example/search-v2");
        using HttpClient client = CreateClient(handler, options => options.AllowAutoRedirect = false);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
        response.Headers.Location.ShouldBe(new Uri("https://origin.example/search-v2"));
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: a 3xx without a Location field is not followed")]
    public async Task SendAsync_RedirectWithoutLocation_ShouldNotFollow()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        var handler = new ScriptedRedirectHandler()
            .EnqueueRedirect(HttpStatusCode.Found, location: null);
        using HttpClient client = CreateClient(handler);

        // Act
        using HttpResponseMessage response = await client.SendAsync(
            CreateQuery("https://origin.example/search"), cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ClientFactory] - Redirect: MaxAutomaticRedirections rejects non-positive values")]
    public void MaxAutomaticRedirections_NonPositive_ShouldThrow()
    {
        var options = new NamedHttpClientOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxAutomaticRedirections = 0);
    }
}
