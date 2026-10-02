using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.OpenApi.Tests;

/// <summary>
/// How each part of an operation is derived from endpoint metadata: paths from the route template,
/// parameters from the generated parameter descriptions (or the template's constraints), bodies and
/// responses from the generated descriptions and the registered serializers, tags, summaries and
/// descriptions from the convention verbs, and security requirements from authorization metadata.
/// </summary>
public class OpenApiOperationDescriptionTests
{
    private static async Task<OpenApiDocument> GetOrdersDocumentAsync()
    {
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateOrdersApi();
        using HttpClient client = factory.CreateClient();

        return await OpenApiTestApplication.GetDocumentAsync(client, "/openapi/v1.json", cancellation.Token);
    }

    private static OpenApiOperation GetOperation(OpenApiDocument document, string path, OperationType method)
        => document.Paths!.Items[path].Operations[method];

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: a typed GET is described from its route name, conventions and generated description")]
    public async Task Describe_TypedGet_ShouldUseNameConventionsAndDescription()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders/{id}", OperationType.Get);

        // Assert — the template's {id:long} is the path parameter; the nullable result lists 204.
        operation.OperationId.ShouldBe("getOrder");
        operation.Summary.ShouldBe("Gets an order");
        operation.Description.ShouldBe("Returns the order with the given identifier.");
        operation.Tags.ShouldBe(["orders"]);

        OpenApiParameter id = operation.Parameters.ShouldHaveSingleItem();
        id.Name.ShouldBe("id");
        id.In.ShouldBe(ParameterLocation.Path);
        id.Required.ShouldBeTrue();
        id.Schema!.Type.ShouldBe(SchemaType.Integer);
        id.Schema.Format.ShouldBe("int64");

        operation.Responses!.Items.Keys.ShouldBe(["200", "204", "400", "406"]);
        OpenApiResponse ok = operation.Responses.Items["200"];
        ok.Description.ShouldBe("OK");
        ok.Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/Order");
        operation.Responses.Items["204"].Content.ShouldBeEmpty();
        operation.Responses.Items["204"].Description.ShouldBe("No Content");
        operation.Security.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: query and header inputs become parameters; reserved headers are left out")]
    public async Task Describe_QueryAndHeaderInputs_ShouldBecomeParameters()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders", OperationType.Get);

        // Assert — the Authorization header is described by security, not as a parameter (the OpenAPI Parameter Object ignores it).
        operation.Parameters.Select(parameter => parameter.Name).ShouldBe(["page", "X-Tenant"]);

        OpenApiParameter page = operation.Parameters[0];
        page.In.ShouldBe(ParameterLocation.Query);
        page.Required.ShouldBeFalse();
        page.Schema!.Type.ShouldBe(SchemaType.Integer);
        page.Schema.Format.ShouldBe("int32");

        OpenApiParameter tenant = operation.Parameters[1];
        tenant.In.ShouldBe(ParameterLocation.Header);
        tenant.Required.ShouldBeTrue();
        tenant.Schema!.Type.ShouldBe(SchemaType.String);

        OpenApiSchema list = operation.Responses!.Items["200"].Content["application/json"].Schema!;
        list.Type.ShouldBe(SchemaType.Array);
        list.Items!.Reference!.Ref.ShouldBe("#/components/schemas/Order");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: a JSON body becomes a required request body with its contract's schema")]
    public async Task Describe_BodyInput_ShouldBecomeRequestBody()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders", OperationType.Post);

        // Assert — tags from the group and the route compose, group first.
        operation.OperationId.ShouldBe("createOrder");
        operation.Tags.ShouldBe(["orders", "writes"]);
        operation.Parameters.ShouldBeEmpty();
        operation.RequestBody!.Required.ShouldBeTrue();
        operation.RequestBody.Content.Keys.ShouldBe(["application/json"]);
        operation.RequestBody.Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/CreateOrder");
        operation.Responses!.Items.Keys.ShouldBe(["200", "400", "406", "415"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: the binding thunk's own outcomes are described with the RFC 9457 schema")]
    public async Task Describe_BindingOutcomes_ShouldUseProblemDetails()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders", OperationType.Post);

        // Assert
        OpenApiResponse badRequest = operation.Responses!.Items["400"];
        badRequest.Description.ShouldBe("Bad Request");
        badRequest.Content["application/problem+json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/ProblemDetails");
        operation.Responses.Items["415"].Content.Keys.ShouldBe(["application/problem+json"]);
        operation.Responses.Items["406"].Content.ShouldBeEmpty();

        // The OpenApi writer sorts map keys, so a served document lists properties alphabetically.
        OpenApiSchema problem = document.Components!.Schemas["ProblemDetails"];
        problem.Properties.Keys.ShouldBe(["type", "title", "status", "detail", "instance"], ignoreOrder: true);
        problem.Properties["status"].Type.ShouldBe(SchemaType.Integer);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: form fields become a urlencoded object body and a string result is text/plain")]
    public async Task Describe_FormInputs_ShouldBecomeFormBody()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders/{id}/status", OperationType.Put);

        // Assert
        operation.Parameters.ShouldHaveSingleItem().Name.ShouldBe("id");

        OpenApiSchema form = operation.RequestBody!.Content["application/x-www-form-urlencoded"].Schema!;
        operation.RequestBody.Required.ShouldBeTrue();
        form.Type.ShouldBe(SchemaType.Object);
        form.Properties.Keys.ShouldBe(["status", "priority"], ignoreOrder: true);
        form.Properties["priority"].Type.ShouldBe(SchemaType.Integer);
        form.Required.ShouldBe(["status"]);

        OpenApiResponse ok = operation.Responses!.Items["200"];
        ok.Content.Keys.ShouldBe(["text/plain"]);
        ok.Content["text/plain"].Schema!.Type.ShouldBe(SchemaType.String);
        operation.Responses.Items.ShouldNotContainKey("415");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: a group prefix parameter bound as route-or-query is a path parameter")]
    public async Task Describe_RouteOrQueryInGroup_ShouldResolveAgainstTemplate()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/tenants/{tenant}/orders", OperationType.Get);

        // Assert
        operation.Parameters.Select(parameter => (parameter.Name, parameter.In)).ShouldBe([("tenant", ParameterLocation.Path), ("q", ParameterLocation.Query)]);
        operation.Responses!.Items["200"].Content["application/json"].Schema!.Reference!.Ref.ShouldBe("#/components/schemas/PageOfOrder");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: an endpoint requiring authorization lists its declared scheme")]
    public async Task Describe_RequireAuthorization_ShouldListSecurityRequirement()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();
        OpenApiOperation operation = GetOperation(document, "/orders", OperationType.Post);

        // Assert — no scheme named: the default authenticate scheme establishes the principal.
        OpenApiSecurityRequirement requirement = operation.Security.ShouldHaveSingleItem();
        requirement.Schemes.Keys.ShouldBe(["Bearer"]);
        requirement.Schemes["Bearer"].ShouldBeEmpty();

        OpenApiSecurityScheme scheme = document.Components!.SecuritySchemes["Bearer"];
        scheme.Type.ShouldBe(SecuritySchemeType.Http);
        scheme.Scheme.ShouldBe("bearer");
        scheme.BearerFormat.ShouldBe("JWT");
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: raw endpoints appear only when described, typed from their constraints")]
    public async Task Describe_RawEndpoints_ShouldAppearOnlyWhenDescribed()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();

        // Assert
        document.Paths!.Items.Keys.ShouldNotContain("/raw/{name}");
        document.Paths.Items.Keys.ShouldNotContain("/internal/cache");

        OpenApiOperation described = GetOperation(document, "/described/{id}", OperationType.Get);
        described.Summary.ShouldBe("A raw endpoint the application described");
        OpenApiParameter id = described.Parameters.ShouldHaveSingleItem();
        id.Schema!.Type.ShouldBe(SchemaType.Integer);
        id.Schema.Format.ShouldBe("int32");
        id.Schema.Minimum.ShouldBe(1);
        described.Responses!.Items.Keys.ShouldBe(["200"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: a QUERY endpoint is described for OpenAPI 3.2 and left out below it")]
    public async Task Describe_QueryMethod_ShouldAppearOnlyFor32()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(OpenApiTestApplication.Timeout);
        await using WebApplicationTestFactory factory = OpenApiTestApplication.CreateFactory();
        factory.Application.UseRouting();
        factory.Application.Map(Assimalign.Cohesion.Http.HttpMethod.Query, "/orders/search", (CreateOrder filter) => new Page<Order>([], 0));
        factory.Application.MapGet("/ping", () => "pong");

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/ping", cancellation.Token);

        // Act
        OpenApiDocument line32 = factory.Application.GetOpenApiDescriptionProvider().GetDocument(OpenApiSpecVersion.V3_2);
        OpenApiDocument line31 = factory.Application.GetOpenApiDescriptionProvider().GetDocument(OpenApiSpecVersion.V3_1);

        // Assert — QUERY carries a body (RFC 10008) and exists as an operation field only from 3.2.
        line32.Paths!.Items["/orders/search"].Operations[OperationType.Query].RequestBody!.Content.Keys.ShouldBe(["application/json"]);
        line31.Paths!.Items.Keys.ShouldBe(["/ping"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.OpenApi] - Operations: document tags list declared tags first, then tags endpoints use")]
    public async Task Describe_Tags_ShouldListDeclaredThenUsed()
    {
        // Arrange / Act
        OpenApiDocument document = await GetOrdersDocumentAsync();

        // Assert
        document.Tags.Select(tag => tag.Name).ShouldBe(["orders", "writes"]);
        document.Tags[0].Description.ShouldBe("Order operations");
        document.Tags[1].Description.ShouldBeNull();
    }
}
