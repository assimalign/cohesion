using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Unit coverage for cache-key construction: request-target and <c>VaryBy*</c> partitioning of the
/// primary key, order-independent query folding, and the secondary (variant) key derived from the
/// stored response's <c>Vary</c> header — the mechanism that keeps a variant a client cannot accept
/// unreachable to it.
/// </summary>
public class OutputCacheKeyBuilderTests
{
    private static string PrimaryKey(OutputCacheTestContext context, OutputCachePolicy policy)
        => OutputCacheKeyBuilder.BuildPrimaryKey(context, policy, routeValues: null);

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: The same request yields the same primary key")]
    public void BuildPrimaryKey_SameRequest_ShouldMatch()
    {
        // Arrange
        OutputCachePolicy policy = new();
        OutputCacheTestContext a = new() { Request = { Path = new HttpPath("/a") } };
        OutputCacheTestContext b = new() { Request = { Path = new HttpPath("/a") } };

        // Act / Assert
        PrimaryKey(a, policy).ShouldBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: A different path yields a different primary key")]
    public void BuildPrimaryKey_DifferentPath_ShouldDiffer()
    {
        // Arrange
        OutputCachePolicy policy = new();
        OutputCacheTestContext a = new() { Request = { Path = new HttpPath("/a") } };
        OutputCacheTestContext b = new() { Request = { Path = new HttpPath("/b") } };

        // Act / Assert
        PrimaryKey(a, policy).ShouldNotBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: Query folding is order-independent by default")]
    public void BuildPrimaryKey_QueryOrder_ShouldNotMatter()
    {
        // Arrange
        OutputCachePolicy policy = new();
        OutputCacheTestContext a = new();
        a.Request.QueryCollection.Add("a", new HttpQueryValue("1"));
        a.Request.QueryCollection.Add("b", new HttpQueryValue("2"));

        OutputCacheTestContext b = new();
        b.Request.QueryCollection.Add("b", new HttpQueryValue("2"));
        b.Request.QueryCollection.Add("a", new HttpQueryValue("1"));

        // Act / Assert
        PrimaryKey(a, policy).ShouldBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: A different query value yields a different key")]
    public void BuildPrimaryKey_DifferentQueryValue_ShouldDiffer()
    {
        // Arrange
        OutputCachePolicy policy = new();
        OutputCacheTestContext a = new();
        a.Request.QueryCollection.Add("x", new HttpQueryValue("1"));
        OutputCacheTestContext b = new();
        b.Request.QueryCollection.Add("x", new HttpQueryValue("2"));

        // Act / Assert
        PrimaryKey(a, policy).ShouldNotBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: VaryByQuery ignores unlisted query keys")]
    public void BuildPrimaryKey_VaryByQuerySubset_ShouldIgnoreOtherKeys()
    {
        // Arrange — only 'page' participates; 'trace' must not fragment the cache.
        OutputCachePolicy policy = new();
        policy.VaryByQuery("page");

        OutputCacheTestContext a = new();
        a.Request.QueryCollection.Add("page", new HttpQueryValue("1"));
        a.Request.QueryCollection.Add("trace", new HttpQueryValue("abc"));

        OutputCacheTestContext b = new();
        b.Request.QueryCollection.Add("page", new HttpQueryValue("1"));
        b.Request.QueryCollection.Add("trace", new HttpQueryValue("xyz"));

        // Act / Assert
        PrimaryKey(a, policy).ShouldBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: VaryByHeader partitions on the request header value")]
    public void BuildPrimaryKey_VaryByHeader_ShouldPartition()
    {
        // Arrange
        OutputCachePolicy policy = new();
        policy.VaryByHeader("X-Tenant");

        OutputCacheTestContext a = new();
        a.Request.Headers["X-Tenant"] = "acme";
        OutputCacheTestContext b = new();
        b.Request.Headers["X-Tenant"] = "globex";

        // Act / Assert
        PrimaryKey(a, policy).ShouldNotBe(PrimaryKey(b, policy));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Key: A variant key partitions on the response Vary header value")]
    public void BuildVariantKey_DifferentVaryValue_ShouldDiffer()
    {
        // Arrange — the response varies by Accept-Encoding: a br client and a gzip client must land on
        // different variant keys so neither can receive the other's stored representation.
        string[] vary = { "Accept-Encoding" };
        OutputCacheTestContext brClient = new();
        brClient.Request.Headers["Accept-Encoding"] = "br";
        OutputCacheTestContext gzipClient = new();
        gzipClient.Request.Headers["Accept-Encoding"] = "gzip";

        string brKey = OutputCacheKeyBuilder.BuildVariantKey("primary", brClient.Request, vary);
        string gzipKey = OutputCacheKeyBuilder.BuildVariantKey("primary", gzipClient.Request, vary);

        // Act / Assert
        brKey.ShouldNotBe(gzipKey);
    }
}
