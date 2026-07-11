using System;
using System.Net.Http;
using System.Text;
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
/// Full-pipeline coverage over the in-memory test factory: the registry is composed at builder
/// time, seeded onto each exchange by the runtime, and consumed by middleware through the typed
/// call-site extensions.
/// </summary>
public class SerializationPipelineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Pipeline: Should round-trip typed request and response bodies end to end")]
    public async Task Pipeline_TypedBodyRoundTrip_ShouldSerializeEndToEnd()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            TestOrder? order = await context.Request.ReadContentAsync<TestOrder>(context.RequestCancelled);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;

            TestReceipt receipt = new(order!.Id, order.Quantity * 4.25m, order.Quantity > 2);
            await context.Response.WriteContentAsync(receipt, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("""{"id":"ord-7","quantity":3}""", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/orders", content, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("application/json; charset=utf-8");
        (await response.Content.ReadAsStringAsync(cancellationToken))
            .ShouldBe("""{"orderId":"ord-7","total":12.75,"expedited":true}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - Pipeline: Should let middleware turn an unsupported media type into a 415 outcome")]
    public async Task Pipeline_UnsupportedMediaType_ShouldBranchTo415Outcome()
    {
        // Arrange — the non-throwing registry surface is how middleware produces protocol
        // outcomes (415) instead of faults for unsupported request formats.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddJsonSerialization(TestJsonContext.Default);

        factory.Application.Use(async (context, next) =>
        {
            IHttpContentSerializationFeature registry = context.Features.Get<IHttpContentSerializationFeature>()!;

            if (!context.Request.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue header)
                || !HttpMediaType.TryParse(header.Value, out HttpMediaType contentType)
                || registry.GetReader(contentType) is null)
            {
                context.Response.StatusCode = 415;
                return;
            }

            TestOrder? order = await context.Request.ReadContentAsync<TestOrder>(context.RequestCancelled);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.WriteContentAsync(new TestReceipt(order!.Id, 0m, false), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("a,b,c", Encoding.UTF8, "text/csv");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/orders", content, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.UnsupportedMediaType);
    }
}
