# Assimalign.Cohesion.Web.Testing

Socketless integration testing for Cohesion web applications. `WebApplicationTestFactory`
composes a `WebApplication` against the in-memory connection driver
(`Assimalign.Cohesion.Connections.InMemory`) — **no sockets, no ports** — manages its
lifecycle per test, and hands out `System.Net.Http.HttpClient` instances wired through
`SocketsHttpHandler.ConnectCallback`, so every request flows the application's full pipeline
(middleware, routing, features) end to end. This is how Cohesion consumers integration-test
their services, and how Cohesion itself gets deterministic h1/h2 pipeline tests in CI.

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
- AOT/trim-safe: pure builder-time delegate wiring over BCL client types, zero reflection.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
