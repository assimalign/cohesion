using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// Redirect rules: the status and <c>Location</c> for each redirect status, the statuses rejected at
/// registration, the query a redirect keeps, replaces or removes, absolute targets and fragments, the
/// percent-encoded <c>Location</c>, and the guard that keeps a capture from turning a redirect into a
/// network-path reference to another host.
/// </summary>
public class RewriteRedirectTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: Each redirect status should be answered with the Location and end the pipeline")]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task AddRedirect_EachStatus_ShouldAnswerStatusAndLocation(int status)
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/old/(\\d+)$", "/new/$1", status));
        builder.Run(probe.InvokeAsync);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old/5"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(status);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/new/5");
        context.Response.Body.Length.ShouldBe(0);
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A redirect without a status should answer 302 Found")]
    public async Task AddRedirect_DefaultStatus_ShouldBeFound()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/old$", "/new"));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(302);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A status other than 301, 302, 307 or 308 should be rejected at registration")]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(303)]
    [InlineData(304)]
    [InlineData(404)]
    public void AddRedirect_InvalidStatus_ShouldThrow(int status)
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirect("^/a$", "/b", status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirect(_ => true, "/b", status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToHttps(status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToWww(status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToNonWww(status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToTrailingSlash(status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToNoTrailingSlash(status));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToLowercase(status));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A target without '?' should carry the request's query")]
    public async Task AddRedirect_TargetWithoutQuery_ShouldKeepQuery()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/old$", "/new", HttpStatusCode.MovedPermanently));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old", "a=1&b=x%20y&flag"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/new?a=1&b=x%20y&flag");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A target with a query should replace the request's, and a trailing '?' should drop it")]
    [InlineData("/new?v=2", "/new?v=2")]
    [InlineData("/new?", "/new")]
    public async Task AddRedirect_TargetWithQuery_ShouldReplaceQuery(string replacement, string location)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/old$", replacement, HttpStatusCode.MovedPermanently));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old", "v=1"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(location);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: The Location should be percent-encoded")]
    public async Task AddRedirect_DecodedPathCapture_ShouldEncodeLocation()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/old/(.+)$", "/new/$1?ref=$1", HttpStatusCode.PermanentRedirect));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old/café/100%"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/new/caf%C3%A9/100%25?ref=caf%C3%A9%2F100%25");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: An absolute target should be written with its captures encoded for each part")]
    public async Task AddRedirect_AbsoluteTarget_ShouldEncodeCapturesPerPart()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/s/(.+)$", "https://search.example/find/$1?q=$1#top", HttpStatusCode.Found));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/s/a&b=cd"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://search.example/find/a&b=cd?q=a%26b%3Dcd#top");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: An absolute target should keep the request's query when it has no '?'")]
    public async Task AddRedirect_AbsoluteTargetWithoutQuery_ShouldKeepQuery()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/docs/(.*)$", "https://docs.example/$1", HttpStatusCode.MovedPermanently));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/docs/intro", "lang=en"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://docs.example/intro?lang=en");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A capture should not turn the Location into a reference to another host")]
    public async Task AddRedirect_CaptureStartingWithSlashes_ShouldStayOnOrigin()
    {
        // Arrange — "/go//evil.example" captures "/evil.example"; "//evil.example" would leave the origin.
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/go/(.*)$", "/$1", HttpStatusCode.Found));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/go//evil.example/x"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/evil.example/x");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A backslash in a capture should be encoded, not read as a slash")]
    public async Task AddRedirect_CaptureWithBackslash_ShouldEncodeIt()
    {
        // Arrange — browsers read "/\host" as "//host".
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/go(.*)$", "$1", HttpStatusCode.Found));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/go/\\evil.example"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/%5Cevil.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A redirect target may carry a fragment")]
    public async Task AddRedirect_TargetWithFragment_ShouldWriteFragment()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect("^/faq/(\\d+)$", "/help#question-$1", HttpStatusCode.MovedPermanently));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/faq/12", "src=nav"));

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/help?src=nav#question-12");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Redirect: A redirect over the path and query should match the re-encoded query")]
    public async Task AddRedirect_PathAndQueryTarget_ShouldMoveQueryValueIntoPath()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRedirect(
            "^/article\\.aspx\\?id=(\\d+)$",
            "/articles/$1?",
            HttpStatusCode.MovedPermanently,
            RewriteMatchTarget.PathAndQuery));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/article.aspx", "id=314"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(301);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/articles/314");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Registration: A malformed redirect target should be rejected when the rule is added")]
    [InlineData("relative/path")]
    [InlineData("ftp://files.example/")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    [InlineData("/b/$3")]
    public void AddRedirect_MalformedTarget_ShouldThrowArgument(string replacement)
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.AddRedirect("^/(a)$", replacement));
    }
}
