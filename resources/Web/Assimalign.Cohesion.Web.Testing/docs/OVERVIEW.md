# Assimalign.Cohesion.Web.Testing — Overview

## Purpose

Full-pipeline integration testing for Cohesion web applications without an operating-system
socket. The package's one entry point, `WebApplicationTestFactory`, composes a
`WebApplication` whose default server listens on the in-memory connection driver, manages the
server's lifecycle per test (start on first use, graceful drain on dispose), and creates
`System.Net.Http.HttpClient` instances that dial the in-memory listener through
`SocketsHttpHandler.ConnectCallback`. A request sent through such a client crosses the real
HTTP wire format, the real transport receive loop, the real server dispatch, and the
application's real middleware pipeline — deterministically, on every CI operating system,
with no ports to allocate or collide on.

## Scope

- `IWebApplicationTestFactory` / `WebApplicationTestFactory` — the per-test application host.
- `WebApplicationTestFactoryOptions` — protocol selection and client base address.
- `WebApplicationTestProtocol` — `Http1` (default) or prior-knowledge `Http2`. HTTP/3 is a
  documented non-goal (see `DESIGN.md`).

The factory intentionally has no assertion helpers, no fixture base classes, and no test
framework coupling — it composes and hosts; the test framework and assertion library are the
caller's business.

## Usage

```csharp
await using WebApplicationTestFactory factory = new();

// 1. Builder-time configuration (services, features, extra listener options).
factory.Builder.AddRouting();

// 2. Pipeline configuration on the built application.
factory.Application.Use(async (context, next) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync("hello"u8.ToArray(), context.RequestCancelled);
});

// 3. Send requests; the server starts on the first client.
using HttpClient client = factory.CreateClient();
string payload = await client.GetStringAsync("/");
```

Prior-knowledge HTTP/2 over the same in-memory pair:

```csharp
await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
{
    Protocol = WebApplicationTestProtocol.Http2,
});
```

## Dependencies

- `Assimalign.Cohesion.Web.Hosting` — the `WebApplicationBuilder` / `WebApplication`
  composition root the factory drives.
- `Assimalign.Cohesion.Http.Connections` — the `UseHttp1` / `UseHttp2` listener registration
  seams.
- `Assimalign.Cohesion.Connections.InMemory` — the in-memory transport driver (listener +
  dialing factory).
- `Assimalign.Cohesion.Connections` — the `Connection` contract and duplex-pipe stream
  adapter the client side rides.

The client side is otherwise pure BCL (`SocketsHttpHandler`, `HttpClient`).

## Design

See [`DESIGN.md`](DESIGN.md) for the composition model, lifecycle contract, protocol scope
(including why HTTP/3 is out), parallel-isolation guarantees, and AOT posture.
