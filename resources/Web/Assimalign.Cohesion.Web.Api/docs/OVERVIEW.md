# Assimalign.Cohesion.Web.Api Overview

`Web.Api` is the endpoint-mapping surface over `Web.Routing`. It offers plain terminal-middleware
mapping and, through the `Assimalign.Cohesion.SourceGeneration.Web` source generator, AOT-safe
typed-delegate parameter binding.

## Typed Endpoints

```csharp
app.AddRouting();          // builder time
app.UseRouting();          // pipeline time

// `id` is bound from the matched route value; the returned user is written as the response.
app.MapGet("/users/{id}", (int id) => store.FindAsync(id));

app.MapPost("/orders", async (Order order, IHttpContext context) =>
{
    // `order` is deserialized from the request body via the serialization registry; `context` is injected.
    context.Response.StatusCode = HttpStatusCode.Created;
});
```

A handler can be a lambda or a method group (`app.MapGet("/users/{id}", GetUser)`).

Parameters bind from the request by convention or by explicit attribute:

| Source | Attribute | Notes |
| --- | --- | --- |
| Route value | `[FromRoute]` | Inferred when the name matches a `{token}` in the pattern |
| Query string | `[FromQuery]` | Default for scalar parameters |
| Header | `[FromHeader]` | Explicit only |
| Body | `[FromBody]` | Default for complex parameters; one per handler |
| Form field | `[FromForm]` | Per-field scalars |
| `IHttpContext` | — | Injected directly |
| `CancellationToken` | — | Bound from `RequestCancelled` |
| `IHttpFeature` types | — | Resolved from `context.Features` |

Unparseable or missing-required scalars produce a 400 problem+json (with an `errors` extension naming
the parameter); an unsupported body Content-Type produces 415; a malformed body produces 400.

## Return Values

A handler that returns a value — directly, or through `Task<T>` or `ValueTask<T>` — has it written as
the response. There are no result types: a handler that needs control of the response sets it on
`IHttpContext`.

| The handler returns | The response |
| --- | --- |
| Nothing (`void`, `Task`, `ValueTask`) | Whatever the handler wrote |
| A `string` | The text as UTF-8, `text/plain; charset=utf-8` unless the handler set a `Content-Type` |
| `null` | No body; `204 No Content` unless the handler set another status |
| Any other value | Serialized through the content-serialization registry for the request's `Accept`, with `Vary: Accept`; `406` when nothing registered is acceptable |

The status is 200 unless the handler set one: `context.Response.StatusCode = HttpStatusCode.Created;
return order;` answers 201 with the order. A serialized type needs a contract in the registered
resolver (`[JsonSerializable(typeof(Order))]` on the application's `JsonSerializerContext`); a missing
contract or registry throws `HttpContentSerializationException` to the exception boundary (a 500), never
a reflection fallback.

## Compile-Time Diagnostics

A handler the source generator cannot bind fails the build with a `COHWEB` error that says what to
write instead, rather than throwing when the endpoint is mapped: a delegate instance in place of a
lambda (COHWEB0001), a return type an endpoint cannot write such as a `Stream` or `async void`
(COHWEB0002), a parameter that cannot be bound (COHWEB0003), two request bodies (COHWEB0004), a body
with form fields (COHWEB0005), a delegate type generated code cannot name (COHWEB0006), and a body or
serialized return without `Web.Serialization` referenced (COHWEB0007). The table is in
[DESIGN.md](DESIGN.md#compile-time-diagnostics-1059).

## Endpoint Metadata and Groups

Every `Map*` returns the mapped route's `IRouterRouteBuilder`, so per-endpoint policies attach where
the endpoint is mapped. Groups hold typed endpoints too, and group metadata reaches every child
whenever it is attached:

```csharp
app.MapGet("/users/{id:int}", async (int id, IHttpContext context) => { /* ... */ })
   .WithName("user")                       // link generation
   .WithMetadata(new AuditMetadata("pii")); // any metadata item

IRouterGroupBuilder api = app.MapGroup("api/{tenant}");
api.MapGet("orders/{id:int}", async (string tenant, int id, IHttpContext context) => { /* ... */ });
api.RequireHost("api.example.com");        // applies to every endpoint in the group
```

A group endpoint binds a parameter its own template does not name from the route value first (the
group prefix supplies `tenant` above), then from the query string.

## Endpoint Descriptions

Every typed endpoint also describes itself, for documentation adapters such as the OpenAPI adapter:
an `EndpointParameterMetadata` per request-bound parameter (name, source, CLR type, required) and
`EndpointResponseMetadata` for its responses (status, the written CLR type, `text/plain` for a string,
and a `204` when the result may be `null`). Read them from a built route, or from the matched endpoint
during a request:

```csharp
foreach (IRouterRoute route in router.Routes)
{
    IReadOnlyList<EndpointParameterMetadata> parameters = route.Metadata.GetOrderedMetadata<EndpointParameterMetadata>();
    IReadOnlyList<EndpointResponseMetadata> responses = route.Metadata.GetOrderedMetadata<EndpointResponseMetadata>();
    // parameters[0]: Name "id", Source Route, Type typeof(long), IsRequired true
    // responses[0]:  StatusCode 200, Type typeof(Order), ContentType null (negotiated)
}

// Describe another response the handler can answer; it composes with the generated ones.
app.MapGet("/orders/{id}", (long id) => orders.Get(id))
   .WithMetadata(new EndpointResponseMetadata(HttpStatusCode.NotFound));
```

The types are `typeof(...)` values written by the source generator, so a schema comes from the
application's source-generated `JsonTypeInfo`, never from reflection.

Four convention verbs curate the description on a route or a whole group, without depending on any
documentation format: `WithTags` (tags compose, group first), `WithSummary` and `WithDescription` (the
most specific wins), and `ExcludeFromDescription`. The OpenAPI adapter, `Assimalign.Cohesion.Web.OpenApi`
(a NuGet package), maps them onto operation tags, summaries and descriptions:

```csharp
IRouterGroupBuilder orders = app.MapGroup("orders").WithTags("orders");
orders.MapGet("{id:long}", (long id) => store.Find(id))
      .WithSummary("Gets an order")
      .WithDescription("Returns the order with the given identifier.");
app.MapGet("/internal/cache", () => "cleared").ExcludeFromDescription();
```

## Wiring

- Reference the generator: `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />`
  (automatic for Sdk.Web consumers).
- Allow-list the generated namespace:
  `<InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.Web.Api.Generated</InterceptorsNamespaces>`.
- Body binding and serialized return values need `Web.Serialization` (`AddJsonSerialization(...)` with
  the application's source-generated `JsonSerializerContext`); form binding needs `Http.Forms`. Both
  are carried by the `App.Web` shared framework.
- Form-bound endpoints require antiforgery when the application references
  `Assimalign.Cohesion.Web.Antiforgery` (every `Sdk.Web` application does): register
  `AddAntiforgery(...)` and `UseAntiforgery()` after `UseRouting()`, or opt an endpoint out with
  `.DisableAntiforgery()`. Without the middleware those endpoints fail at dispatch.
