using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// Full-pipeline coverage of the negotiated write over the in-memory test factory: the registry is
/// composed at builder time, and <c>WriteNegotiatedContentAsync</c> selects the representation from
/// the request's <c>Accept</c> header, stamps <c>Vary: Accept</c>, and turns an unacceptable request
/// into a bodyless <c>406</c> outcome.
/// </summary>
public class ContentNegotiationPipelineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiated pipeline: Should serialize the accepted media type and stamp Vary: Accept")]
    public async Task Pipeline_AcceptableRequest_ShouldWriteNegotiatedBodyAndVary()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            bool written = await context.WriteNegotiatedContentAsync(
                new TestReceipt("ord-1", 10m, false), context.RequestCancelled);
            written.ShouldBeTrue();
        });

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Act
        using HttpResponseMessage response = await client.GetAsync("/receipt", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("application/json; charset=utf-8");
        response.Headers.Vary.ShouldContain("Accept");
        (await response.Content.ReadAsStringAsync(cancellationToken))
            .ShouldContain("\"orderId\":\"ord-1\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiated pipeline: Should compose a bodyless 406 when no representation is acceptable")]
    public async Task Pipeline_UnacceptableRequest_ShouldComposeBodyless406()
    {
        // Arrange — only JSON is registered; a client that accepts XML only cannot be served.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            bool written = await context.WriteNegotiatedContentAsync(
                new TestReceipt("ord-2", 0m, false), context.RequestCancelled);
            written.ShouldBeFalse();
        });

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        // Act
        using HttpResponseMessage response = await client.GetAsync("/receipt", cancellationToken);

        // Assert — a bodyless 406 (no Content-Type) is the shape the #881 status-code-pages
        // middleware upgrades into a problem+json explanation.
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotAcceptable);
        response.Content.Headers.ContentType.ShouldBeNull();
        response.Headers.Vary.ShouldContain("Accept");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiated pipeline: Should append Accept to an existing Vary without clobbering it")]
    public async Task Pipeline_ExistingVary_ShouldAppendAcceptWithoutClobbering()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            // A CORS-style layer already varies by Origin; negotiation must add Accept, not replace.
            context.Response.Headers[HttpHeaderKey.Vary] = "Origin";
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.WriteNegotiatedContentAsync(new TestReceipt("ord-3", 1m, true), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Act
        using HttpResponseMessage response = await client.GetAsync("/receipt", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.Vary.ShouldContain("Origin");
        response.Headers.Vary.ShouldContain("Accept");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Negotiated pipeline: Should expose the negotiated media type through the context query")]
    public async Task Pipeline_TryNegotiateContentType_ShouldResolveFromTheExchange()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            bool negotiated = context.TryNegotiateContentType(out HttpMediaType selected);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers["X-Negotiated"] = negotiated ? selected.ToString() : "none";
            await Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Act
        using HttpResponseMessage response = await client.GetAsync("/receipt", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.GetValues("X-Negotiated").ShouldContain("application/json");
    }
}
