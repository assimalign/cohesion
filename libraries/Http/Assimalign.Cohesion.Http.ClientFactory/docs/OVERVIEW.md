# Assimalign.Cohesion.Http.ClientFactory — Overview

A lifecycle-managed factory for named `System.Net.Http.HttpClient` instances. Applications
register named clients on `HttpClientFactoryBuilder` (base address, timeout, default headers,
handler tuning), build an `IHttpClientFactory`, and call `Create(name)` wherever a client is
needed — disposing the returned client freely without exhausting ephemeral ports.

## Scope

- **Named client registration** — per-name `NamedHttpClientOptions`: `BaseAddress`,
  `RequestTimeout`, `ConfigureDefaultHeaders`, `ConfigureHandler` (SocketsHttpHandler tuning),
  `HandlerFactory` (test/stub injection), and the redirect policy
  (`AllowAutoRedirect`, `MaxAutomaticRedirections`).
- **Handler pooling and rotation** — one shared `HttpMessageHandler` per name inside a lifetime
  window (`HandlerLifetime`, default two minutes); rotation refreshes DNS/TLS state, and expired
  handlers are disposed once no client references them.
- **Automatic redirects** — a factory-owned redirect layer with RFC 10008 § 2.5 method
  semantics: QUERY (and every non-POST method) is re-issued with its content on
  `301`/`302`/`307`/`308`; `303 See Other` is fulfilled with a GET; the historical POST→GET
  rewrite on `301`/`302` is preserved.

## Dependencies

`Assimalign.Cohesion.Http` (the protocol core) plus the BCL `System.Net.Http` stack the clients
ride. This is a client-side library: it does not depend on the server transport
(`Http.Connections`) or any Web-area project.

## Usage

```csharp
IHttpClientFactory factory = new HttpClientFactoryBuilder()
    .AddClient("search", options =>
    {
        options.BaseAddress = new Uri("https://api.example.com");
        options.RequestTimeout = TimeSpan.FromSeconds(10);
    })
    .Build();

using HttpClient client = factory.Create("search");
using var query = new HttpRequestMessage(new HttpMethod("QUERY"), "/items")
{
    Content = new StringContent("{\"q\":\"cohesion\"}", Encoding.UTF8, "application/json"),
};
using HttpResponseMessage response = await client.SendAsync(query, cancellationToken);
```

Design rationale (handler pooling shape, redirect-policy ownership, non-goals) lives in
[DESIGN.md](DESIGN.md).
