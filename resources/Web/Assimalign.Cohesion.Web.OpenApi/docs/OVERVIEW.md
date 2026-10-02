# Assimalign.Cohesion.Web.OpenApi — Overview

OpenAPI documents for Cohesion Web applications, generated from the metadata endpoints already carry
rather than from runtime reflection, so the same document is produced under NativeAOT.

## Scope

- **The Web adapter** for `OpenApi.Integration`'s `IOpenApiEndpointSource`, over the application's route
  table: paths from route templates, parameters and responses from the source-generated endpoint
  descriptions, request and response schemas from the application's source-generated System.Text.Json
  contracts, tags, summaries and exclusion from the Web.Api description verbs, and security requirements
  from Web.Authorization metadata.
- **`AddOpenApi(options => ...)`** registers the document: title, API version, description, the OpenAPI
  line (3.1 by default; 3.0 and 3.2 as well), declared security schemes and tags, extra endpoint sources,
  and document transformers.
- **`MapOpenApi(pattern)`** serves the document from a `GET` route as JSON, or YAML for a `.yaml`/`.yml`
  pattern. The document is built on the first request, cached, and revalidated with a strong `ETag`.
- **`GetOpenApiDescriptionProvider()`** returns the same document as a model, for tools and tests.

## Dependencies

- Web: the root, `Web.Api` (endpoint descriptions and the description verbs), `Web.Routing`,
  `Web.Serialization` (the JSON writer's contracts), `Web.Authorization`/`Web.Authentication`,
  `Web.ProblemDetails`.
- OpenApi: `OpenApi.Integration` (contract, provider, exporter), `OpenApi.Attributes` (metadata),
  and through them the model, generation, serialization, versioning and validation.
- Shipped as a NuGet package, not as a member of the `App.Web` shared framework: an application that
  documents its API references it; one that does not carries none of the OpenApi family.

## Usage

```csharp
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.OpenApi;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddRouting();
builder.AddJsonSerialization(AppJsonContext.Default);
builder.AddOpenApi(options =>
{
    options.Title = "Orders API";
    options.ApiVersion = "1.0.0";
    options.AddSecurityScheme(new OpenApiSecuritySchemeMetadata
    {
        Name = "Bearer",                       // the authentication scheme's name
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

await using WebApplication app = builder.Build();
app.UseRouting();

IRouterGroupBuilder orders = app.MapGroup("orders").WithTags("orders");
orders.MapGet("{id:long}", (long id) => store.Find(id))
    .WithName("getOrder")
    .WithSummary("Gets an order");
orders.MapPost("", (CreateOrder order) => store.Add(order))
    .RequireAuthorization();
app.MapGet("/internal/cache", () => "cleared").ExcludeFromDescription();

app.MapOpenApi();                        // GET /openapi/v1.json  (OpenAPI 3.1, JSON)
app.MapOpenApi("/openapi/v1.yaml");      // the same document as YAML

await app.RunAsync();
```

Every type a typed endpoint reads or returns must be in the `JsonSerializerContext` passed to
`AddJsonSerialization`, as it must for the endpoint to serialize it; a missing one fails the document
request with an exception naming the endpoint.

See [DESIGN.md](./DESIGN.md) for how each element of the document is derived, the packaging decision,
the schema pipeline, and the security-requirement rules.
