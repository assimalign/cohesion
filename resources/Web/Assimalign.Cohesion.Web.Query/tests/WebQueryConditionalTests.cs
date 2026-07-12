using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Query.Tests;

/// <summary>
/// RFC 10008 &#167; 2.6 tests for conditional QUERY handling: the precondition helpers parse the
/// request's <c>If-*</c> fields and reuse the core <c>HttpConditionalRequest</c> evaluator, and
/// <c>UseQueryConditionals</c> answers <c>304</c> / <c>412</c> end to end without executing the
/// query — exactly as the equivalent conditional GET.
/// </summary>
public class WebQueryConditionalTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly NetHttpMethod QueryMethod = new("QUERY");
    private static readonly DateTimeOffset LastModified = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ============================================================================
    // Helper surface (unit level — header parsing + outcome shaping)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: a matching If-None-Match evaluates NotModified on QUERY")]
    public void EvaluateQueryPreconditions_IfNoneMatchMatches_ShouldBeNotModified()
    {
        // Arrange
        var context = new TestHttpContext("/search", CohesionHttpMethod.Query);
        context.Request.Headers[HttpHeaderKey.IfNoneMatch] = "\"v2\"";

        var validators = new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") };

        // Act + Assert
        context.EvaluateQueryPreconditions(in validators).ShouldBe(HttpPreconditionOutcome.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: a malformed precondition field is ignored")]
    public void EvaluateQueryPreconditions_MalformedIfNoneMatch_ShouldProceed()
    {
        // Arrange — an unparseable If-None-Match is treated as absent (the RFC 9110 §13.1.3
        // posture the core date parsing encodes), not as a failed precondition.
        var context = new TestHttpContext("/search", CohesionHttpMethod.Query);
        context.Request.Headers[HttpHeaderKey.IfNoneMatch] = "not-an-entity-tag";

        var validators = new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") };

        // Act + Assert
        context.EvaluateQueryPreconditions(in validators).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: TryHandleQueryPreconditions writes 304 with the validators")]
    public void TryHandleQueryPreconditions_NotModified_ShouldWrite304WithValidators()
    {
        // Arrange
        var context = new TestHttpContext("/search", CohesionHttpMethod.Query);
        context.Request.Headers[HttpHeaderKey.IfNoneMatch] = "\"v2\"";

        var validators = new WebQueryResourceValidators
        {
            ETag = HttpEntityTag.Strong("v2"),
            LastModified = LastModified,
        };

        // Act
        bool handled = context.TryHandleQueryPreconditions(in validators);

        // Assert — RFC 9110 §15.4.5: the 304 carries the validator fields the 200 would have.
        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        context.Response.Headers.GetValue(HttpHeaderKey.ETag).ShouldBe("\"v2\"");
        context.Response.Headers.GetValue(HttpHeaderKey.LastModified).ShouldBe(HttpDate.Format(LastModified));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: TryHandleQueryPreconditions writes 412 on a failed If-Match")]
    public void TryHandleQueryPreconditions_FailedIfMatch_ShouldWrite412()
    {
        // Arrange
        var context = new TestHttpContext("/search", CohesionHttpMethod.Query);
        context.Request.Headers[HttpHeaderKey.IfMatch] = "\"v1\"";

        var validators = new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") };

        // Act
        bool handled = context.TryHandleQueryPreconditions(in validators);

        // Assert
        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: no preconditions proceed without writing")]
    public void TryHandleQueryPreconditions_NoPreconditions_ShouldProceed()
    {
        // Arrange
        var context = new TestHttpContext("/search", CohesionHttpMethod.Query);
        var validators = new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") };

        // Act + Assert
        context.TryHandleQueryPreconditions(in validators).ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    // ============================================================================
    // UseQueryConditionals — end to end over the in-memory transport
    // ============================================================================

    private static async Task<(WebApplicationTestFactory Factory, HttpClient Client, StrongBox<bool> QueryExecuted)> CreateConditionalAppAsync(
        WebQueryResourceValidators? validators,
        CancellationToken cancellationToken)
    {
        var factory = new WebApplicationTestFactory();
        var executed = new StrongBox<bool>();

        factory.Application.UseQueryConditionals(_ => ValueTask.FromResult(validators));
        factory.Application.Use(async (context, next) =>
        {
            executed.Value = true;
            context.Response.StatusCode = HttpStatusCode.Ok;
            byte[] payload = Encoding.UTF8.GetBytes("results");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        await factory.StartAsync(cancellationToken);
        return (factory, factory.CreateClient(), executed);
    }

    private sealed class StrongBox<T>
    {
        public T? Value;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: a matching If-None-Match answers 304 without executing the query (RFC 10008 §2.6)")]
    public async Task UseQueryConditionals_IfNoneMatchMatches_ShouldAnswer304WithoutExecuting()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2"), LastModified = LastModified },
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(QueryMethod, "/search")
        {
            Content = new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"v2\"").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — 304 with the validators; the query never ran.
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe("\"v2\"");
        executed.Value.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: an If-Modified-Since date in the future answers 304 on QUERY")]
    public async Task UseQueryConditionals_IfModifiedSinceNotModified_ShouldAnswer304()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            new WebQueryResourceValidators { LastModified = LastModified },
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(QueryMethod, "/search")
        {
            Content = new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Modified-Since", HttpDate.Format(LastModified)).ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        executed.Value.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: a failed If-Match answers 412 without executing the query")]
    public async Task UseQueryConditionals_FailedIfMatch_ShouldAnswer412WithoutExecuting()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") },
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(QueryMethod, "/search")
        {
            Content = new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"v1\"").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.PreconditionFailed);
        executed.Value.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: an unconditional QUERY runs and its 200 carries the validators")]
    public async Task UseQueryConditionals_UnconditionalQuery_ShouldExecuteAndStampValidators()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2"), LastModified = LastModified },
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(QueryMethod, "/search")
        {
            Content = new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
        };

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — the query ran, and the response advertises the validators for the client's
        // next conditional query.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("results");
        response.Headers.ETag.ShouldNotBeNull();
        executed.Value.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: unknown validators pass the query through untouched")]
    public async Task UseQueryConditionals_ProviderReturnsNull_ShouldPassThrough()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            validators: null,
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(QueryMethod, "/search")
        {
            Content = new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"v2\"").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — no validators, no precondition evaluation: the query runs.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        executed.Value.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Conditional: non-QUERY requests pass through untouched")]
    public async Task UseQueryConditionals_NonQueryMethod_ShouldPassThrough()
    {
        // Arrange — a GET with a matching If-None-Match is not this middleware's decision.
        using CancellationTokenSource cancellation = new(TestTimeout);
        (WebApplicationTestFactory factory, HttpClient client, var executed) = await CreateConditionalAppAsync(
            new WebQueryResourceValidators { ETag = HttpEntityTag.Strong("v2") },
            cancellation.Token);
        await using WebApplicationTestFactory ownedFactory = factory;
        using HttpClient ownedClient = client;

        using var request = new HttpRequestMessage(NetHttpMethod.Get, "/search");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"v2\"").ShouldBeTrue();

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        executed.Value.ShouldBeTrue();
    }
}
