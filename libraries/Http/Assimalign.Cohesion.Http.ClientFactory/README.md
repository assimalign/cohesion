# Assimalign.Cohesion.Http.ClientFactory

Lifecycle-managed `HttpClient` factory for the Cohesion HTTP family.

`IHttpClientFactory` returns named `System.Net.Http.HttpClient`
instances backed by a pool of rotating
`System.Net.Http.SocketsHttpHandler`s. Periodic handler rotation
mitigates the classic socket-exhaustion / stale-DNS trade-off that
plagues naive `HttpClient` lifetime management.

## Why a factory

| Anti-pattern | Failure |
|--------------|---------|
| `new HttpClient()` per request | Each instance owns a fresh socket pool. Disposal leaves sockets in TIME_WAIT. Under load: ephemeral port exhaustion. |
| Single static `HttpClient`, lifetime = app | DNS resolution cached forever inside `SocketsHttpHandler`. Failover doesn't propagate until process restart. |

The factory threads the needle: clients are cheap to obtain and
cheap to dispose; handlers underneath get pooled and rotated on a
schedule (default 2 minutes). Within the window every client built
for the same name shares the underlying handler &mdash; so
connections are reused. Once the window elapses the active handler
is moved to an expired list, a fresh handler takes its place, and
the expired one is disposed when garbage collection reclaims the
last `HttpClient` still using it.

This is the same lifecycle shape Microsoft's reference
[`IHttpClientFactory`](https://learn.microsoft.com/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests)
uses, rebuilt without the `Microsoft.Extensions.*` dependency.

## Surface

| Type | Role |
|------|------|
| `IHttpClientFactory` | Public consumption surface &mdash; `Create(string name)` returns a configured `HttpClient`. Implements `IDisposable` and `IAsyncDisposable`. |
| `HttpClientFactoryBuilder` | Fluent registration of named clients + factory-wide settings. |
| `HttpClientFactoryOptions` | Default handler lifetime + injectable `TimeProvider` + the named-client registry. |
| `NamedHttpClientOptions` | Per-name configuration: base address, request timeout, handler lifetime override, default headers, custom `SocketsHttpHandler` configuration, optional `HandlerFactory` override (mostly for tests). |

## Usage

```csharp
using Assimalign.Cohesion.Http;

await using IHttpClientFactory factory = new HttpClientFactoryBuilder()
    .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddClient("api", o =>
    {
        o.BaseAddress    = new Uri("https://api.example.com");
        o.RequestTimeout = TimeSpan.FromSeconds(15);
        o.ConfigureDefaultHeaders = h => h.Add("User-Agent", "cohesion-app/1.0");
    })
    .AddClient("metrics", o =>
    {
        o.BaseAddress      = new Uri("https://metrics.internal");
        o.HandlerLifetime  = TimeSpan.FromMinutes(30);   // long-lived internal hop
        o.ConfigureHandler = sockets =>
        {
            sockets.AutomaticDecompression = System.Net.DecompressionMethods.All;
            sockets.MaxConnectionsPerServer = 32;
        };
    })
    .Build();

// Hot path: cheap to call, cheap to dispose. Underlying handler is shared
// across every "api" client built within the 5-minute window.
using HttpClient apiClient = factory.Create("api");
HttpResponseMessage response = await apiClient.GetAsync("/v1/health");
```

## Lifecycle guarantees (proven by the test suite)

- **Within the lifetime window** every `Create("name")` call returns
  a fresh `HttpClient` backed by the **same** `HttpMessageHandler`
  &mdash; so connections are reused.
- **After the window elapses** the next `Create` rotates the
  handler. The previous handler stays alive for any
  `HttpClient`s that still hold it.
- **Concurrent `Create` calls** never produce more than one handler
  per rotation interval. A 200-thread flood that hits the same name
  ends with handler-creation count = 1.
- **Expired handlers** get disposed once garbage collection reclaims
  the last `HttpClient` using them. Until then, in-flight requests
  on the old handler complete cleanly.
- **`Dispose` / `DisposeAsync`** disposes every pooled handler
  &mdash; active and expired &mdash; and rejects further
  `Create` calls with `ObjectDisposedException`.

## Standards alignment

This package does **not** implement an HTTP protocol. Protocol
behavior is delegated to `System.Net.Http.HttpClient` (which uses
`SocketsHttpHandler` underneath). The factory's value is purely
lifecycle management. RFC compliance is whatever the BCL provides.

## Non-goals

- **Typed clients** (`AddTypedClient<T>`-style). Microsoft's factory
  supports them via DI. Cohesion does not embed DI in the same way;
  a future package can layer typed-client conventions on top if
  there's demand.
- **Resilience policies** (Polly-style retry / circuit-breaker /
  hedging). Resilience belongs to `Assimalign.Cohesion.Resilience`;
  consumers compose the two layers themselves.
- **A custom HTTP message handler stack**. The factory is a
  lifecycle wrapper around the BCL's `SocketsHttpHandler`. Outbound
  HTTP/1.1, HTTP/2, HTTP/3 negotiation is whatever the platform
  provides.
