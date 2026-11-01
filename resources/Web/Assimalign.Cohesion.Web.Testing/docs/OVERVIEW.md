# Assimalign.Cohesion.Web.Testing — Overview

## Purpose

Full-pipeline integration testing for composed Web applications over the in-memory connection
driver.

## Scope

- `IWebApplicationTestFactory` / `WebApplicationTestFactory` — the per-test application host.
- `WebApplicationTestFactoryOptions` — protocol selection, client base address, and content root.
- `WebApplicationTestProtocol` — `Http1` (default) or prior-knowledge `Http2`. HTTP/3 is a
  documented non-goal (see `DESIGN.md`).

The factory intentionally has no assertion helpers, no fixture base classes, and no test
framework coupling — it composes and hosts; the test framework and assertion library are the
caller's business.

## Usage

```csharp
await using WebApplicationTestFactory factory = new();

// 1. Builder-time configuration (services, features, extra listener options).
factory.Builder.Services.AddRouting();

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

Point the application at a content root on disk (its `wwwroot` becomes the web root that
`UseStaticFiles()` serves; the default is the test assembly's base directory):

```csharp
await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
{
    ContentRootPath = FileSystemPath.Parse(contentRootDirectory),
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
- `Assimalign.Cohesion.Hosting` — the host lifecycle the application runs on.
- `Assimalign.Cohesion.DependencyInjection` — the builder's `Services` registrations.

The client side is otherwise pure BCL (`SocketsHttpHandler`, `HttpClient`).

## Design

See [`DESIGN.md`](DESIGN.md) for the composition model, lifecycle contract, protocol scope
(including why HTTP/3 is out), parallel-isolation guarantees, and AOT posture.

The socketless client transport consumes `IDuplexConnection` for its input/output pipe.
Identity and lifetime remain available through the shared `IConnection` root; HTTP listener
accepts use `AcceptAsync` and retain their accepted connections until `ConnectionClosed` fires.
