using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// The policy model: build-time validation of origins, methods and header names, the combinations
/// <see cref="CorsPolicyBuilder.Build"/> rejects, and the matching rules the built policy applies.
/// </summary>
public class CorsPolicyBuilderTests
{
    // ------------------------------------------------------------------ origins

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithOrigins: An origin should be normalized to the serialized form a browser sends")]
    [InlineData("https://app.example", "https://app.example")]
    [InlineData("HTTPS://App.Example", "https://app.example")]
    [InlineData("https://app.example:443", "https://app.example")]
    [InlineData("http://app.example:80", "http://app.example")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("http://localhost:08080", "http://localhost:8080")]
    [InlineData("https://app.example:8443", "https://app.example:8443")]
    [InlineData("http://127.0.0.1:3000", "http://127.0.0.1:3000")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080", "http://[::1]:8080")]
    [InlineData("https://[2001:DB8:0:0:0:0:0:1]", "https://[2001:db8::1]")]
    [InlineData("chrome-extension://abcdefghijklmnop", "chrome-extension://abcdefghijklmnop")]
    public void WithOrigins_ValidOrigin_ShouldNormalizeToSerializedForm(string configured, string serialized)
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        CorsPolicy policy = builder.WithOrigins(configured).Build();

        // Assert
        policy.Origins.ShouldBe([serialized]);
        policy.IsOriginAllowed(serialized).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithOrigins: A value that is not a serialized origin should be rejected when it is added")]
    [InlineData("", "empty")]
    [InlineData("*", "AllowAnyOrigin")]
    [InlineData("null", "SetIsOriginAllowed")]
    [InlineData("https://app.example/", "trailing '/'")]
    [InlineData("https://app.example/path", "Remove '/path'")]
    [InlineData("https://app.example?x=1", "Remove '?x=1'")]
    [InlineData("https://app.example#top", "Remove '#top'")]
    [InlineData("https://user@app.example", "user information")]
    [InlineData("https://*.app.example", "wildcards")]
    [InlineData("app.example", "scheme://host[:port]")]
    [InlineData("https://", "no host")]
    [InlineData("https://:443", "no host")]
    [InlineData("https://app.example:", "port number")]
    [InlineData("https://app.example:65536", "port number")]
    [InlineData("https://app.example:http", "port number")]
    [InlineData("https://bücher.example", "punycode")]
    [InlineData(" https://app.example", "whitespace")]
    [InlineData("https://app example", "whitespace")]
    [InlineData("1https://app.example", "not a URL scheme")]
    [InlineData("https://[::1", "IPv6")]
    [InlineData("https://[fe80::1%25eth0]", "IPv6")]
    [InlineData("https://[127.0.0.1]", "IPv6")]
    [InlineData("https://[::1]x", "':port'")]
    [InlineData("https://app_example.com!", "not a host name")]
    public void WithOrigins_InvalidOrigin_ShouldThrowArgumentException(string configured, string expectedMessageFragment)
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.WithOrigins(configured);

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain(expectedMessageFragment);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - WithOrigins: A null origin list should throw ArgumentNullException")]
    public void WithOrigins_NullList_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.WithOrigins(null!);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - WithOrigins: Origins that normalize alike should be kept once, in order")]
    public void WithOrigins_DuplicateAfterNormalization_ShouldKeepFirstOccurrence()
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        CorsPolicy policy = builder
            .WithOrigins("https://b.example", "https://A.example", "https://a.example:443")
            .WithOrigins("https://b.example")
            .Build();

        // Assert
        policy.Origins.ShouldBe(["https://b.example", "https://a.example"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - IsOriginAllowed: A listed origin should match exactly, never by prefix or case")]
    public void IsOriginAllowed_ListedOrigin_ShouldMatchExactly()
    {
        // Arrange
        CorsPolicy policy = new CorsPolicyBuilder().WithOrigins("https://app.example").Build();

        // Act / Assert
        policy.IsOriginAllowed("https://app.example").ShouldBeTrue();
        policy.IsOriginAllowed("https://app.example.evil").ShouldBeFalse();
        policy.IsOriginAllowed("http://app.example").ShouldBeFalse();
        policy.IsOriginAllowed("https://APP.example").ShouldBeFalse();
        policy.IsOriginAllowed("https://app.example:8443").ShouldBeFalse();
        policy.IsOriginAllowed("null").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - IsOriginAllowed: A predicate should admit origins beyond the list and see only well-formed origins")]
    public void IsOriginAllowed_Predicate_ShouldUnionWithListAndSeeOnlySerializedOrigins()
    {
        // Arrange
        int calls = 0;
        CorsPolicy policy = new CorsPolicyBuilder()
            .WithOrigins("https://app.example")
            .SetIsOriginAllowed(origin =>
            {
                calls++;
                return origin.EndsWith(".tenant.example", StringComparison.Ordinal) && origin.StartsWith("https://", StringComparison.Ordinal);
            })
            .Build();

        // Act / Assert
        policy.IsOriginAllowed("https://app.example").ShouldBeTrue();
        policy.IsOriginAllowed("https://acme.tenant.example").ShouldBeTrue();
        policy.IsOriginAllowed("http://acme.tenant.example").ShouldBeFalse();
        int callsForWellFormed = calls;
        policy.IsOriginAllowed("https://evil.example/.tenant.example").ShouldBeFalse();
        policy.IsOriginAllowed("HTTPS://ACME.tenant.example").ShouldBeFalse();
        calls.ShouldBe(callsForWellFormed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - IsOriginAllowed: A predicate should see the opaque origin null and decide it deliberately")]
    public void IsOriginAllowed_PredicateAndOpaqueOrigin_ShouldConsultPredicate()
    {
        // Arrange
        CorsPolicy policy = new CorsPolicyBuilder().SetIsOriginAllowed(origin => origin == "null").Build();

        // Act / Assert
        policy.IsOriginAllowed("null").ShouldBeTrue();
        policy.Origins.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - IsOriginAllowed: An any-origin policy should allow every origin")]
    public void IsOriginAllowed_AnyOrigin_ShouldAllowEveryOrigin()
    {
        // Arrange
        CorsPolicy policy = new CorsPolicyBuilder().AllowAnyOrigin().Build();

        // Act / Assert
        policy.AllowsAnyOrigin.ShouldBeTrue();
        policy.IsOriginAllowed("https://anything.example").ShouldBeTrue();
        policy.IsOriginAllowed("null").ShouldBeTrue();
    }

    // ------------------------------------------------------------------ combinations

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: A policy that allows no origin should throw")]
    public void Build_NoOrigin_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithMethods("PUT");

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("at least one origin");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Any origin with credentials should throw, as Fetch forbids it")]
    public void Build_AnyOriginWithCredentials_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().AllowAnyOrigin().AllowCredentials();

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("AllowCredentials");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Any origin combined with a listed origin should throw")]
    public void Build_AnyOriginWithListedOrigin_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().AllowAnyOrigin().WithOrigins("https://app.example");

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Any origin combined with an origin predicate should throw")]
    public void Build_AnyOriginWithPredicate_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().AllowAnyOrigin().SetIsOriginAllowed(_ => true);

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Any method combined with a method list should throw")]
    public void Build_AnyMethodWithMethodList_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://app.example").AllowAnyMethod().WithMethods("PUT");

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: Any header combined with a header list should throw")]
    public void Build_AnyHeaderWithHeaderList_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://app.example").AllowAnyHeader().WithHeaders("X-Trace");

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: A credentialed policy with listed origins should build")]
    public void Build_CredentialsWithListedOrigins_ShouldBuild()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://app.example").AllowCredentials();

        // Act
        CorsPolicy policy = builder.Build();

        // Assert
        policy.SupportsCredentials.ShouldBeTrue();
        policy.AllowsAnyOrigin.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Build: A policy built earlier should not change when the builder changes")]
    public void Build_BuilderChangedAfterwards_ShouldNotAffectEarlierPolicy()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://a.example");
        CorsPolicy first = builder.Build();

        // Act
        CorsPolicy second = builder.WithOrigins("https://b.example").WithMethods("PUT").Build();

        // Assert
        first.Origins.ShouldBe(["https://a.example"]);
        first.Methods.ShouldBeEmpty();
        second.Origins.ShouldBe(["https://a.example", "https://b.example"]);
    }

    // ------------------------------------------------------------------ methods and headers

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - WithMethods: Fetch-normalized methods should be uppercased and other methods kept as written")]
    public void WithMethods_MixedCase_ShouldApplyFetchNormalization()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://app.example");

        // Act
        CorsPolicy policy = builder.WithMethods("put", "Delete", "PATCH", "patch", "PUT").Build();

        // Assert
        policy.Methods.ShouldBe(["PUT", "DELETE", "PATCH", "patch"]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithMethods: A value that is not a method token should be rejected")]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("PU T")]
    [InlineData("GET,PUT")]
    [InlineData("DELETE\r\n")]
    public void WithMethods_InvalidMethod_ShouldThrowArgumentException(string method)
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.WithMethods(method);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithHeaders: A value that is not a header name should be rejected")]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("X Trace")]
    [InlineData("X-Trace:")]
    [InlineData("X-Trace,X-Other")]
    public void WithHeaders_InvalidName_ShouldThrowArgumentException(string header)
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.WithHeaders(header);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Cors] - WithExposedHeaders: A value that is not a header name should be rejected")]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("X Total")]
    public void WithExposedHeaders_InvalidName_ShouldThrowArgumentException(string header)
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.WithExposedHeaders(header);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - WithHeaders: Header names should be kept once, compared case-insensitively")]
    public void WithHeaders_DuplicateDifferingInCase_ShouldKeepFirstOccurrence()
    {
        // Arrange
        CorsPolicyBuilder builder = new CorsPolicyBuilder().WithOrigins("https://app.example");

        // Act
        CorsPolicy policy = builder
            .WithHeaders("Content-Type", "X-Trace", "content-type")
            .WithExposedHeaders("ETag", "etag", "X-Total")
            .Build();

        // Assert
        policy.Headers.ShouldBe(["Content-Type", "X-Trace"]);
        policy.ExposedHeaders.ShouldBe(["ETag", "X-Total"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - SetPreflightMaxAge: A negative max age should throw")]
    public void SetPreflightMaxAge_Negative_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.SetPreflightMaxAge(TimeSpan.FromSeconds(-1));

        // Assert
        act.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - SetIsOriginAllowed: A null predicate should throw ArgumentNullException")]
    public void SetIsOriginAllowed_NullPredicate_ShouldThrow()
    {
        // Arrange
        CorsPolicyBuilder builder = new();

        // Act
        Action act = () => builder.SetIsOriginAllowed(null!);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }
}
