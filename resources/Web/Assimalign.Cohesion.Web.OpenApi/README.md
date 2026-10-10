# Assimalign.Cohesion.Web.OpenApi

OpenAPI documents for Cohesion Web applications, generated from endpoint metadata instead of runtime
reflection, so the document is the same under NativeAOT. The package describes the application's routes
through the `OpenApi.Integration` endpoint-source contract (parameters and responses from the
source-generated endpoint descriptions, schemas from the application's source-generated System.Text.Json
contracts, security requirements from each endpoint's effective authorization policy) and serves the
document with `MapOpenApi` as JSON or YAML, for OpenAPI 3.0, 3.1 or 3.2.

```csharp
builder.Services.AddOpenApi(options => options.Title = "Orders API");
// ...
app.MapOpenApi();   // GET /openapi/v1.json
```

It ships as a NuGet package rather than with the `App.Web` shared framework: reference it from an
application that documents its API.

- [docs/OVERVIEW.md](./docs/OVERVIEW.md) — scope, dependencies, and usage
- [docs/DESIGN.md](./docs/DESIGN.md) — how the document is derived, and why
