# Assimalign.Cohesion.Web.Testing

Integration testing for Cohesion web applications. `WebApplicationTestFactory` supports two
complementary modes:

- `new WebApplicationTestFactory()` composes a mutable application over the in-memory
  connection driver for deterministic HTTP/1.1 and HTTP/2 pipeline tests.
- `WebApplicationTestFactory.FromProgram<Program>()` invokes an enabled resource's real entry
  point under a test-scoped `Hosting.Resources` `ResourceRuntime.CreateScope(...)`, waits for
  `/readyz`, and stops it through `/cohesion/v1/stop`.

```csharp
await using WebApplicationTestFactory factory = new();

factory.Builder.AddRouting();
factory.Application.UseRouting().Map(new Route(HttpMethod.Get, "/widgets", new RouterRouteHandler(async context =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync("[]"u8.ToArray(), context.RequestCancelled);
})));

using HttpClient client = factory.CreateClient(); // starts the server on first use

HttpResponseMessage response = await client.GetAsync("/widgets");
```

- HTTP/1.1 by default; prior-knowledge HTTP/2 via
  `new WebApplicationTestFactoryOptions { Protocol = WebApplicationTestProtocol.Http2 }`.
  HTTP/3 is out of scope (QUIC-bound — see `docs/DESIGN.md`).
- Factories are fully isolated: run as many as you like in parallel in one process.
- The Program-backed path uses the compiler-rooted `Assembly.EntryPoint` operation sanctioned
  by the developer-experience design; generated registration preserves the entry for AOT.

Program-backed use:

```csharp
await using IWebApplicationProgramTestFactory factory =
    WebApplicationTestFactory.FromProgram<Program>();

using HttpClient client = factory.CreateClient();
HttpResponseMessage readiness = await client.GetAsync("/readyz");
```

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
