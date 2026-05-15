# Assimalign.Cohesion.Http.Sessions

Per-exchange session state for the Cohesion HTTP family.

## Why a separate package

The Cohesion HTTP protocol core (`Assimalign.Cohesion.Http`) defines only
wire-level concepts &mdash; request, response, headers, methods, status,
targets, body streams. HTTP sessions are an **application-layer**
concept and are not part of any HTTP RFC. Protocol-only consumers
(`HttpClient` wrappers, reverse proxies, edge caches, observability
layers, the DNS-over-HTTPS transport, ...) never need a session.

Keeping sessions in a separate package means the protocol core stays
small, AOT-friendly, and free of application state assumptions; the
session abstraction layers on top of `IHttpContext.Items` so the core
stays single-purpose.

## Surface

| Type | Role |
|------|------|
| `IHttpSession` | Abstract session contract &mdash; identifier, key/value bag, load/commit lifecycle |
| `HttpSession` | In-memory implementation |
| `HttpContextSessionExtensions` | `GetSession()` / `SetSession()` / `RequireSession()` extensions on `IHttpContext` |

## Usage

```csharp
using Assimalign.Cohesion.Http;

// On the server side (per request), attach a session to the context:
var session = new HttpSession();
await session.LoadAsync(cancellationToken);
context.SetSession(session);

// Anywhere downstream:
IHttpSession session = context.RequireSession();
session.SetString("user-id", "42");
await session.CommitAsync(cancellationToken);
```

## Implementing a custom backing store

`HttpSession` is in-memory only. For Redis / distributed backends,
implement `IHttpSession` directly and attach it via `SetSession`.
