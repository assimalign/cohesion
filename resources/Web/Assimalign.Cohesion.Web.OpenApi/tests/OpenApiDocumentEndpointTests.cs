using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.OpenApi.Tests;

/// <summary>
/// End-to-end coverage for the document endpoint (#152): a small application with typed endpoints serves
/// its OpenAPI document over the in-memory transport, as JSON and YAML and for every supported line, and
/// the document it serves is valid against the official schemas.
/// </summary>
public class OpenApiDocumentEndpointTests
{
    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: the served JSON document is a valid OpenAPI 3.1 description of the application")]
    public async Task MapOpenApi_JsonDocument_ShouldServeValidDescription()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        // Act
        (HttpResponseMessage response, string text) = await OpenApiTestApplication.GetAsync(client, "/openapi/v1.json", cancellation.Token);
        using HttpResponseMessage _ = response;
        OpenApiDocument document = OpenApiJson.Parse(text);
        OpenApiValidationResult validation = document.Validate();

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        document.Info.Title.ShouldBe("Orders API");
        document.Info.Version.ShouldBe("2.1.0");
        document.Info.Description.ShouldBe("Manages orders.");
        document.Paths!.Items.Keys.ShouldBe(
            ["/orders/{id}", "/orders", "/orders/{id}/status", "/tenants/{tenant}/orders", "/ping", "/described/{id}"],
            ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: a pattern ending in .yaml serves the same document as YAML")]
    public async Task MapOpenApi_YamlPattern_ShouldServeYaml()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        // Act
        (HttpResponseMessage response, string text) = await OpenApiTestApplication.GetAsync(client, "/openapi/v1.yaml", cancellation.Token);
        using HttpResponseMessage _ = response;
        OpenApiDocument document = OpenApiYaml.Parse(text);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/yaml");
        document.Validate().IsValid.ShouldBeTrue();
        document.Paths!.Items["/orders"].Operations[OperationType.Post].OperationId.ShouldBe("createOrder");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: a document mapped for OpenAPI 3.0 is valid for 3.0")]
    public async Task MapOpenApi_SpecVersion30_ShouldServeValid30Document()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        // Act
        (HttpResponseMessage response, string text) = await OpenApiTestApplication.GetAsync(client, "/openapi/v1-3.0.json", cancellation.Token);
        using HttpResponseMessage _ = response;
        OpenApiDocument document = OpenApiJson.Parse(text);
        OpenApiValidationResult validation = document.Validate();

        // Assert — 3.0 has no null type: a nullable component reference is allOf plus nullable.
        text.ShouldStartWith("{");
        text.ShouldContain("\"openapi\": \"3.0", Case.Sensitive);
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_0);
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        OpenApiSchema customer = document.Components!.Schemas["Order"].Properties["customer"];
        customer.Nullable.ShouldBeTrue();
        customer.AllOf.ShouldHaveSingleItem().Reference!.Ref.ShouldBe("#/components/schemas/Customer");
    }

    [Theory(DisplayName = "Cohesion Test [Web.OpenApi] - GetOpenApiDescriptionProvider: the document validates on every supported line")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public async Task GetDocument_EverySupportedLine_ShouldValidate(OpenApiSpecVersion version)
    {
        // Arrange — starting the application builds its router and closes the route table.
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage ping = await client.GetAsync("/ping", cancellation.Token);

        // Act
        OpenApiDocument document = factory.Application.GetOpenApiDescriptionProvider().GetDocument(version);
        OpenApiValidationResult validation = document.Validate();

        // Assert
        ping.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        document.SpecVersion.ShouldBe(version);
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        OpenApiDocument reparsed = OpenApiJson.Parse(document.ToJson(version));
        reparsed.Paths!.Items.Count.ShouldBe(document.Paths!.Items.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: the document is built once and revalidates with its strong ETag")]
    public async Task MapOpenApi_RepeatedRequests_ShouldServeCachedBytesWithETag()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        (HttpResponseMessage first, string firstText) = await OpenApiTestApplication.GetAsync(client, "/openapi/v1.json", cancellation.Token);
        using HttpResponseMessage firstResponse = first;
        EntityTagHeaderValue etag = first.Headers.ETag.ShouldNotBeNull();

        using HttpRequestMessage conditional = new(HttpMethod.Get, "/openapi/v1.json");
        conditional.Headers.IfNoneMatch.Add(etag);
        using HttpRequestMessage head = new(HttpMethod.Head, "/openapi/v1.json");

        // Act
        (HttpResponseMessage second, string secondText) = await OpenApiTestApplication.GetAsync(client, "/openapi/v1.json", cancellation.Token);
        using HttpResponseMessage secondResponse = second;
        using HttpResponseMessage notModified = await client.SendAsync(conditional, cancellation.Token);
        using HttpResponseMessage headResponse = await client.SendAsync(head, cancellation.Token);
        byte[] headBody = await headResponse.Content.ReadAsByteArrayAsync(cancellation.Token);

        // Assert
        etag.IsWeak.ShouldBeFalse();
        secondText.ShouldBe(firstText);
        second.Headers.ETag.ShouldBe(etag);
        notModified.StatusCode.ShouldBe(NetHttpStatusCode.NotModified);
        (await notModified.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
        headResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        headResponse.Content.Headers.ContentLength.ShouldBe(System.Text.Encoding.UTF8.GetByteCount(firstText));
        headBody.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: the document route is not part of the document")]
    public async Task MapOpenApi_DocumentRoute_ShouldBeExcluded()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        // Act
        OpenApiDocument document = await OpenApiTestApplication.GetDocumentAsync(client, "/openapi/v1.json", cancellation.Token);

        // Assert
        document.Paths!.Items.Keys.ShouldNotContain(path => path.StartsWith("/openapi", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: mapping the document without AddOpenApi fails at composition")]
    public async Task MapOpenApi_WithoutAddOpenApi_ShouldThrow()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        // Act / Assert
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => factory.Application.MapOpenApi());
        exception.Message.ShouldContain("AddOpenApi", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - MapOpenApi: an empty pattern is rejected")]
    public async Task MapOpenApi_EmptyPattern_ShouldThrow()
    {
        // Arrange
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory();

        // Act / Assert
        Should.Throw<ArgumentException>(() => factory.Application.MapOpenApi(string.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => factory.Application.MapOpenApi("/openapi.json", (OpenApiSpecVersion)42));
    }
}
