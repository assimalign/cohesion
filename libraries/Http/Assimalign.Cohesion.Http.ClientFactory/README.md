# Assimalign.Cohesion.Http.ClientFactory

Lifecycle-managed `HttpClient` factory for the Cohesion HTTP family.

`IHttpClientFactory` returns named `System.Net.Http.HttpClient`
instances backed by pooled `SocketsHttpHandler`s with periodic
rotation, mitigating the classic socket-exhaustion / stale-DNS
trade-off that plagues naive `HttpClient` lifetime management.

> **Status:** abstraction stubbed; the lifecycle implementation lands in
> a follow-up PR (story `[L01.01.11.12]`).

## Why a factory

| Anti-pattern | Failure |
|--------------|---------|
| `new HttpClient()` per request | Each instance owns a fresh socket pool. Disposal leaves sockets in TIME_WAIT. Under load: ephemeral port exhaustion. |
| Single static `HttpClient`, lifetime = app | DNS resolution cached forever inside `SocketsHttpHandler`. Failover doesn't propagate until process restart. |

The factory threads the needle: clients are cheap to obtain;
handlers underneath get pooled and rotated on a schedule (default
2 minutes), so connections are reused within a window but DNS and
connection state refresh periodically.

## Public surface

```csharp
public interface IHttpClientFactory
{
    HttpClient Create(string name);
}
```

Configuration shape, handler-lifetime tuning, and concrete
`HttpClientFactory` lifecycle land with story `[L01.01.11.12]`.
