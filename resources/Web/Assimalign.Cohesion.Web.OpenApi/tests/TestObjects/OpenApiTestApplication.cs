using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.OpenApi.Tests.TestObjects;

/// <summary>
/// Composes the small order API the end-to-end tests describe: typed endpoints with route, query,
/// header, body and form inputs, a nullable result, a group with a route-or-query prefix parameter, an
/// authorized endpoint, described and undescribed raw endpoints, and an excluded endpoint.
/// </summary>
internal static class OpenApiTestApplication
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static OpenApiSecuritySchemeMetadata BearerScheme { get; } = new()
    {
        Name = "Bearer",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    };

    /// <summary>
    /// An API-key scheme in a header: an authentication scheme other than the default one, which a
    /// policy has to name to be documented.
    /// </summary>
    public static OpenApiSecuritySchemeMetadata ApiKeyScheme { get; } = new()
    {
        Name = "ApiKey",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        ParameterName = "X-Api-Key"
    };

    // A method-group handler whose declared result admits null: the generator describes 200 and 204.
    private static Task<Order?> FindOrder(long id) => Task.FromResult<Order?>(null);

    /// <summary>
    /// Creates a factory with routing, the JSON contracts, authentication defaulting to the
    /// <c>Bearer</c> scheme, authorization (configured by <paramref name="authorization"/>), and OpenAPI
    /// registered.
    /// </summary>
    public static WebApplicationTestFactory CreateFactory(Action<OpenApiOptions>? configure = null, Action<AuthorizationOptions>? authorization = null)
    {
        WebApplicationTestFactory factory = CreateFactoryWithoutAuthorization(configure);
        factory.Builder.AddAuthorization(authorization);
        return factory;
    }

    /// <summary>
    /// Creates a factory like <see cref="CreateFactory"/> for an application that never calls
    /// <c>AddAuthorization</c>.
    /// </summary>
    public static WebApplicationTestFactory CreateFactoryWithoutAuthorization(Action<OpenApiOptions>? configure = null)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Builder.AddJsonSerialization(OpenApiTestJsonContext.Default);
        factory.Builder.AddAuthentication(BearerScheme.Name);
        factory.Builder.AddOpenApi(configure);
        return factory;
    }

    /// <summary>
    /// Creates the order API, including its document endpoints (JSON for the configured line, YAML,
    /// and a 3.0 rendition).
    /// </summary>
    public static WebApplicationTestFactory CreateOrdersApi()
    {
        WebApplicationTestFactory factory = CreateFactory(options =>
        {
            options.Title = "Orders API";
            options.ApiVersion = "2.1.0";
            options.Description = "Manages orders.";
            options.AddSecurityScheme(BearerScheme);
            options.AddTag(new OpenApiTagMetadata { Name = "orders", Description = "Order operations" });
        });

        factory.Application.UseRouting();

        IRouterGroupBuilder orders = factory.Application.MapGroup("orders").WithTags("orders");

        orders.MapGet("{id:long}", FindOrder)
            .WithName("getOrder")
            .WithSummary("Gets an order")
            .WithDescription("Returns the order with the given identifier.");

        orders.MapGet("", ([FromQuery] int? page, [FromHeader(Name = "X-Tenant")] string tenant, [FromHeader(Name = "Authorization")] string? authorization) =>
                (IReadOnlyList<Order>)[])
            .WithName("listOrders");

        orders.MapPost("", (CreateOrder order) => new Order(1, order.Item, 9.5m, OrderStatus.Pending, null, [], DateTimeOffset.UnixEpoch))
            .WithName("createOrder")
            .WithTags("writes")
            .RequireAuthorization();

        orders.MapPut("{id:long}/status", (long id, [FromForm] string status, [FromForm] int? priority) => "accepted")
            .WithName("setStatus");

        IRouterGroupBuilder tenants = factory.Application.MapGroup("tenants/{tenant}");
        tenants.MapGet("orders", (string tenant, [FromQuery(Name = "q")] string? search) => new Page<Order>([], 0))
            .WithName("tenantOrders");

        factory.Application.MapGet("/ping", () => "pong").WithName("ping");
        factory.Application.MapGet("/internal/cache", () => "cleared").ExcludeFromDescription();
        factory.Application.MapGet("/raw/{name:alpha:length(2,10)}", (WebApplicationMiddleware)WriteOkAsync);
        factory.Application.MapGet("/described/{id:int:min(1)}", (WebApplicationMiddleware)WriteOkAsync)
            .WithSummary("A raw endpoint the application described");

        factory.Application.MapOpenApi();
        factory.Application.MapOpenApi("/openapi/v1.yaml");
        factory.Application.MapOpenApi("/openapi/v1-3.0.json", OpenApiSpecVersion.V3_0);

        return factory;
    }

    /// <summary>
    /// Fetches a served document and returns its response and text.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, string Text)> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, text);
    }

    /// <summary>
    /// Fetches and parses the default JSON document.
    /// </summary>
    public static async Task<OpenApiDocument> GetDocumentAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        (HttpResponseMessage response, string text) = await GetAsync(client, path, cancellationToken);
        response.Dispose();
        return path.EndsWith(".yaml", StringComparison.Ordinal) ? OpenApiYaml.Parse(text) : OpenApiJson.Parse(text);
    }

    private static async Task WriteOkAsync(IHttpContext context)
    {
        context.Response.StatusCode = HttpStatusCode.Ok;
        await context.Response.Body.WriteAsync("ok"u8.ToArray(), context.RequestCancelled);
    }
}
