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
small, AOT-friendly, and free of application state assumptions. The
familiar property-style `context.Session` access is restored here
through a .NET 10 extension property, backed by an `IHttpSessionFeature`
stored in `IHttpContext.Features`.

## Surface

| Type | Role |
|------|------|
| `IHttpSession` | Abstract session contract &mdash; `Id`, `IsAvailable`, `Keys`, key/value bag, load/commit lifecycle |
| `HttpSession` | In-memory implementation |
| `HttpSessionExtensions` | Typed accessors over `IHttpSession`: `GetString`/`SetString`, `GetInt32`/`SetInt32`, `TryGetString`/`TryGetInt32` |
| `HttpSessionOptions` | Session configuration (cookie name, idle timeout) |
| `IHttpSessionFeature` | Per-exchange session state stored in `IHttpContext.Features` |
| `HttpSessionFeature` | Default in-memory feature implementation (internal; constructed via the `Session` setter) |
| `HttpContextSessionExtensions` | `context.Session` / `context.RequireSession` extension properties on `IHttpContext` |

## Usage

```csharp
using Assimalign.Cohesion.Http;

// On the server side (per request), attach a session to the context:
HttpSession session = new();
await session.LoadAsync(cancellationToken);
context.Session = session;

// Anywhere downstream:
IHttpSession current = context.RequireSession;
current.SetString("user-id", "42");
await current.CommitAsync(cancellationToken);
```

When no session middleware has run, `context.Session` returns `null` and
`context.RequireSession` throws `InvalidOperationException` &mdash; the
distinction lets opt-in middleware peek without forcing every consumer
into a try/catch.

## Typed access

The store is binary; typed accessors are extension members on
`IHttpSession`, so they work for any implementation:

```csharp
session.SetString("user-id", "42");      // UTF-8
session.SetInt32("cart-count", 3);       // big-endian Int32

string? user = session.GetString("user-id");   // "42"
int? count = session.GetInt32("cart-count");    // 3
bool has = session.TryGetInt32("cart-count", out int n);

foreach (string key in session.Keys) { /* ... */ }
bool ready = session.IsAvailable;        // true once LoadAsync has completed
```

`GetInt32` returns `null` when the stored value is not exactly four bytes, so
a string value is never silently reinterpreted as an int.

## Implementing a custom feature

`HttpSessionFeature` is internal. Middleware that needs richer session
state (e.g. distributed-store metadata, session timeout tracking) should
implement `IHttpSessionFeature` directly and attach it via
`context.Features.Set<IHttpSessionFeature>(...)`. The `context.Session`
getter consults the feature collection for any implementation, not just
the package's default.

## Implementing a custom backing store

`HttpSession` is in-memory only. For Redis / distributed backends,
implement `IHttpSession` directly and attach it via `context.Session = ...`.
