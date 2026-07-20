# Assimalign.Cohesion.Web.Api Overview

`Web.Api` is the endpoint-mapping surface over `Web.Routing`. It offers plain terminal-middleware
mapping and, through the `Assimalign.Cohesion.SourceGeneration.Web` source generator, AOT-safe
typed-delegate parameter binding.

## Typed Endpoints

```csharp
app.AddRouting();          // builder time
app.UseRouting();          // pipeline time

app.MapGet("/users/{id}", async (int id, IHttpContext context) =>
{
    // `id` is bound from the matched route value; `context` is injected.
    await context.Response.WriteContentAsync(await store.FindAsync(id), context.RequestCancelled);
});

app.MapPost("/orders", async (Order order, IHttpContext context) =>
{
    // `order` is deserialized from the request body via the serialization registry.
    context.Response.StatusCode = HttpStatusCode.Created;
});
```

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

## Validation

```csharp
IValidator validator = Validator.Create(c => c.AddProfile(new OrderValidationProfile()));

app.MapPost("/orders", async (Order order, IHttpContext context) => { /* ... */ }, validator);
```

The validator runs against the bound model before the handler; failures short-circuit to a 400
problem+json whose `errors` extension is built from the validation result.

## Wiring

- Reference the generator: `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />`
  (automatic for Sdk.Web consumers).
- Allow-list the generated namespace:
  `<InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.Web.Api.Generated</InterceptorsNamespaces>`.
- Body binding needs `Web.Serialization` (`AddJsonSerialization(...)`); form binding needs
  `Http.Forms`. Both are carried by the `App.Web` shared framework.
