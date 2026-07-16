using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Serialization.Tests;

/// <summary>
/// The built-in JSON pair and the typed call-site extensions: reflection-free round-trips over a
/// source-generated resolver, web-default naming, and the fault surface when a read/write cannot
/// resolve.
/// </summary>
public class JsonContentSerializationTests
{
    private static TestHttpContext ComposeContext()
    {
        TestWebApplicationBuilder builder = new();
        builder.AddJsonSerialization(TestJsonContext.Default);

        TestHttpContext context = new();
        context.Features.Set(builder.Features.OfType<IHttpContentSerializationFeature>().Single());

        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should deserialize the request body from the Content-Type reader")]
    public async Task ReadContentAsync_JsonRequestBody_ShouldDeserialize()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.SetRequestBody("""{"id":"ord-7","quantity":3}""", "application/json; charset=utf-8");

        // Act
        TestOrder? order = await context.Request.ReadContentAsync<TestOrder>(CancellationToken.None);

        // Assert
        order.ShouldNotBeNull();
        order.Id.ShouldBe("ord-7");
        order.Quantity.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should fault when the request has no Content-Type")]
    public async Task ReadContentAsync_MissingContentType_ShouldThrow()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.SetRequestBody("""{"id":"ord-7","quantity":3}""", contentType: null);

        // Act / Assert
        await Should.ThrowAsync<HttpContentSerializationException>(
            async () => await context.Request.ReadContentAsync<TestOrder>(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should fault when no reader matches the media type")]
    public async Task ReadContentAsync_UnregisteredMediaType_ShouldThrow()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.SetRequestBody("a,b,c", "text/csv");

        // Act / Assert
        await Should.ThrowAsync<HttpContentSerializationException>(
            async () => await context.Request.ReadContentAsync<TestOrder>(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should fault when no registry is composed")]
    public async Task ReadContentAsync_NoRegistryComposed_ShouldThrow()
    {
        // Arrange
        TestHttpContext context = new();
        context.SetRequestBody("""{"id":"ord-7","quantity":3}""", "application/json");

        // Act / Assert
        await Should.ThrowAsync<HttpContentSerializationException>(
            async () => await context.Request.ReadContentAsync<TestOrder>(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should let a malformed payload surface the format-native exception")]
    public async Task ReadContentAsync_MalformedJson_ShouldThrowJsonException()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.SetRequestBody("{ not json", "application/json");

        // Act / Assert
        await Should.ThrowAsync<JsonException>(
            async () => await context.Request.ReadContentAsync<TestOrder>(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - ReadContentAsync: Should fault for a type outside the resolver's contracts")]
    public async Task ReadContentAsync_UnregisteredType_ShouldThrowContractFault()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.SetRequestBody("""{"value":"x"}""", "application/json");

        // Act / Assert
        HttpContentSerializationException exception = await Should.ThrowAsync<HttpContentSerializationException>(
            async () => await context.Request.ReadContentAsync<UnregisteredModel>(CancellationToken.None));
        exception.Message.ShouldContain("serialization contract");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - CanRead/CanWrite: Should report contract coverage from the resolver")]
    public void CanReadCanWrite_ResolverCoverage_ShouldReflectContracts()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        builder.AddJsonSerialization(TestJsonContext.Default);
        IHttpContentSerializationFeature feature = builder.Features.OfType<IHttpContentSerializationFeature>().Single();

        // Act / Assert
        feature.Readers.Single().CanRead(typeof(TestOrder)).ShouldBeTrue();
        feature.Readers.Single().CanRead(typeof(UnregisteredModel)).ShouldBeFalse();
        feature.Writers.Single().CanWrite(typeof(TestReceipt)).ShouldBeTrue();
        feature.Writers.Single().CanWrite(typeof(UnregisteredModel)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should emit web-default JSON and stamp the Content-Type")]
    public async Task WriteContentAsync_DefaultWriter_ShouldEmitJsonAndContentType()
    {
        // Arrange
        TestHttpContext context = ComposeContext();

        // Act
        await context.Response.WriteContentAsync(new TestReceipt("ord-7", 12.75m, true), CancellationToken.None);

        // Assert — camelCase comes from the JsonSerializerDefaults.Web options the registration composes.
        context.ResponseBodyText().ShouldBe("""{"orderId":"ord-7","total":12.75,"expedited":true}""");
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("application/json; charset=utf-8");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should not touch the status code")]
    public async Task WriteContentAsync_PresetStatusCode_ShouldNotBeTouched()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.Response.StatusCode = 201;

        // Act
        await context.Response.WriteContentAsync(new TestReceipt("ord-7", 12.75m, false), CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe((HttpStatusCode)201);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should honor an explicit media type")]
    public async Task WriteContentAsync_ExplicitMediaType_ShouldEmitIt()
    {
        // Arrange
        TestHttpContext context = ComposeContext();

        // Act
        await context.Response.WriteContentAsync(new TestReceipt("ord-7", 1m, false), HttpMediaType.Parse("text/json"), CancellationToken.None);

        // Assert
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("text/json; charset=utf-8");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should use the response's declared Content-Type when set")]
    public async Task WriteContentAsync_DeclaredResponseContentType_ShouldSelectWriterFromIt()
    {
        // Arrange
        TestHttpContext context = ComposeContext();
        context.Response.Headers[HttpHeaderKey.ContentType] = "text/json";

        // Act
        await context.Response.WriteContentAsync(new TestReceipt("ord-7", 1m, false), CancellationToken.None);

        // Assert
        context.Response.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType).ShouldBeTrue();
        contentType.Value.ShouldBe("text/json; charset=utf-8");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should reject a wildcard media type")]
    public async Task WriteContentAsync_WildcardMediaType_ShouldThrowArgument()
    {
        // Arrange
        TestHttpContext context = ComposeContext();

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(
            async () => await context.Response.WriteContentAsync(new TestReceipt("ord-7", 1m, false), HttpMediaType.Any, CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Serialization] - WriteContentAsync: Should fault when the registry has no writers")]
    public async Task WriteContentAsync_NoWritersRegistered_ShouldThrow()
    {
        // Arrange
        TestWebApplicationBuilder builder = new();
        builder.AddContentSerialization();
        TestHttpContext context = new();
        context.Features.Set(builder.Features.OfType<IHttpContentSerializationFeature>().Single());

        // Act / Assert
        await Should.ThrowAsync<HttpContentSerializationException>(
            async () => await context.Response.WriteContentAsync(new TestReceipt("ord-7", 1m, false), CancellationToken.None));
    }
}
