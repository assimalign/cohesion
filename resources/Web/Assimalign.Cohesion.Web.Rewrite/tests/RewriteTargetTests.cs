using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// Pattern rules and their targets: capture substitution, the query a target keeps, replaces or removes,
/// matching the path and query, caller-supplied and source-generated regular expressions, the encoding that
/// keeps a capture from adding a query parameter or being decoded twice, and the targets rejected at
/// registration.
/// </summary>
public partial class RewriteTargetTests
{
    [GeneratedRegex("^/blog/(?<year>\\d{4})/(?<slug>[a-z-]+)$")]
    private static partial Regex BlogPost();

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Target: Numbered and named captures should substitute into the target")]
    public async Task AddRewrite_Captures_ShouldSubstitute()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/u/(?<user>[a-z]+)/(\\d+)$", "/users/${user}/posts/$1?all=$0"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/u/ada/42"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/users/ada/posts/42");
        probe.Query!["all"].Value.ShouldBe("/u/ada/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Target: '$$' should be a literal dollar sign")]
    public async Task AddRewrite_DoubledDollar_ShouldBeLiteral()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/price/(\\d+)$", "/prices/$$$1"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/price/5"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/prices/$5");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Target: A source-generated regular expression should drive a rule")]
    public async Task AddRewrite_GeneratedRegex_ShouldMatchAndSubstitute()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite(BlogPost(), "/posts/${slug}?year=${year}"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/blog/2026/url-rewriting"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/posts/url-rewriting");
        probe.Query!["year"].Value.ShouldBe("2026");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Target: A NonBacktracking regular expression should drive a rule")]
    public async Task AddRewrite_NonBacktrackingRegex_ShouldMatchAndSubstitute()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        Regex pattern = new("^/docs/(.+)\\.html$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        builder.UseRewrite(rules => rules.AddRewrite(pattern, "/docs/$1"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/guide/intro.html"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/docs/guide/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Target: A string pattern should be case-sensitive unless it says otherwise")]
    public async Task AddRewrite_StringPattern_ShouldBeCaseSensitive()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/About$", "/about-us")
            .AddRewrite("(?i)^/contact$", "/contact-us"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/about"));
        string? about = probe.Path!.Value.Value;
        await builder.SendAsync(new TestHttpContext("/CONTACT"));

        // Assert
        about.ShouldBe("/about");
        probe.Path!.Value.Value.ShouldBe("/contact-us");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Query: A target without '?' should keep the request's query")]
    public async Task AddRewrite_TargetWithoutQuery_ShouldKeepQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/old$", "/new"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/old", "a=1&b=2");

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Query.ShouldBeSameAs(context.Request.Query);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Query: A target with a query should replace the request's query")]
    public async Task AddRewrite_TargetWithQuery_ShouldReplaceQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/products/(\\d+)$", "/product?id=$1&view=full"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/products/7", "utm=mail"));

        // Assert
        probe.Query!.Count.ShouldBe(2);
        probe.Query["id"].Value.ShouldBe("7");
        probe.Query["view"].Value.ShouldBe("full");
        probe.Query.ContainsKey("utm").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Query: A target ending in '?' should remove the query")]
    public async Task AddRewrite_TargetEndingInQuestionMark_ShouldRemoveQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/clean$", "/landing?"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/clean", "utm=mail"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/landing");
        probe.Query!.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Query: A pattern over the path and query should match the re-encoded query")]
    public async Task AddRewrite_PathAndQueryTarget_ShouldMatchQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite(
            "^/item\\.php\\?id=(\\d+)$",
            "/items/$1?",
            RewriteFlow.Continue,
            RewriteMatchTarget.PathAndQuery));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/item.php", "id=42"));
        string? matched = probe.Path!.Value.Value;
        int matchedQueryCount = probe.Query!.Count;
        await builder.SendAsync(new TestHttpContext("/item.php", "id=42&x=1"));

        // Assert
        matched.ShouldBe("/items/42");
        matchedQueryCount.ShouldBe(0);
        probe.Path!.Value.Value.ShouldBe("/item.php");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Query: A path pattern should not see the query")]
    public async Task AddRewrite_PathTarget_ShouldNotMatchQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("id=", "/never"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/item", "id=1"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/item");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: A path capture placed in the query should not add a parameter")]
    public async Task AddRewrite_PathCaptureIntoQuery_ShouldBeEscaped()
    {
        // Arrange — the decoded path segment carries '&' and '=', which must stay data in the query.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/search/(.+)$", "/find?q=$1"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/search/a&admin=true"));

        // Assert
        probe.Query!.Count.ShouldBe(1);
        probe.Query["q"].Value.ShouldBe("a&admin=true");
        probe.Query.ContainsKey("admin").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: A decoded path capture should not be decoded a second time")]
    public async Task AddRewrite_PercentInDecodedPath_ShouldNotDecodeTwice()
    {
        // Arrange — the transport already decoded "%2541" to "%41"; a second decode would turn it into "A".
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/files/(.+)$", "/docs/$1"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/files/a%41"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/docs/a%41");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: A query capture placed in the path should be decoded once")]
    public async Task AddRewrite_QueryCaptureIntoPath_ShouldDecodeOnce()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite(
            "^/search\\?q=([^&]+)$",
            "/find/$1?",
            RewriteFlow.Continue,
            RewriteMatchTarget.PathAndQuery));
        builder.Run(probe.InvokeAsync);

        // Act — the parsed query value is "café"; the pattern matches its re-encoded form.
        await builder.SendAsync(new TestHttpContext("/search", "q=caf%C3%A9"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/find/café");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: A target that decodes to a path no request can carry should answer 400")]
    public async Task AddRewrite_TargetDecodesToInvalidPath_ShouldAnswerBadRequest()
    {
        // Arrange — a space is not a legal request-path character once decoded, on any transport.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite(
            "^/search\\?q=([^&]+)$",
            "/find/$1",
            RewriteFlow.Continue,
            RewriteMatchTarget.PathAndQuery));
        builder.Run(probe.InvokeAsync);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/search", "q=a%20b"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(400);
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: Literal characters a URL cannot carry should be percent-encoded")]
    public async Task AddRewrite_LiteralNonAsciiText_ShouldBeEncodedThenDecoded()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/menu$", "/café?dish=crème brûlée"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/menu"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/café");
        probe.Query!["dish"].Value.ShouldBe("crème brûlée");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Encoding: A literal percent-encoded triplet should be decoded once")]
    public async Task AddRewrite_LiteralEncodedTriplet_ShouldDecodeOnce()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/a$", "/b%2Dc?x=1%262"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/a"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/b-c");
        probe.Query!["x"].Value.ShouldBe("1&2");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Registration: A malformed rewrite target should be rejected when the rule is added")]
    [InlineData("^/(a)$", "/b/$2")]
    [InlineData("^/(a)$", "/b/${missing}")]
    [InlineData("^/(a)$", "/b/${1")]
    [InlineData("^/(a)$", "")]
    [InlineData("^/(a)$", "b/$1")]
    [InlineData("^/(a)$", "https://example.com/$1")]
    [InlineData("^/(a)$", "/b#top")]
    [InlineData("^/(a)$", "/b%20c")]
    [InlineData("^/(a)$", "/b?=1")]
    public void AddRewrite_MalformedTarget_ShouldThrowArgument(string pattern, string replacement)
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.AddRewrite(pattern, replacement));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Registration: An invalid pattern should be rejected when the rule is added")]
    public void AddRewrite_InvalidPattern_ShouldThrowArgument()
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.AddRewrite("^/(unclosed$", "/b"));
    }
}
