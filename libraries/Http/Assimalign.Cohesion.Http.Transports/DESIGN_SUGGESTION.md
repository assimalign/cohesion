# HTTP Transport Connection Design Suggestion

This document captures a proposed direction for evolving the HTTP transport connection abstractions before any refactor work begins.

## Goal

Separate the HTTP transport model into:

- a base HTTP connection abstraction
- a single-stream HTTP connection abstraction for HTTP/1.1
- a multiplexed HTTP connection abstraction for HTTP/2 and HTTP/3

The main objective is to better reflect HTTP protocol semantics without forcing HTTP/2 and HTTP/3 into an HTTP/1.1-shaped API.

## Recommendation

Keep the root `IHttpConnection` interface small and capability-neutral. Do not make it imply single-stream request handling.

Add:

- `IHttpSingleStreamConnection`
- `IHttpMultiplexConnection`
- `IHttpSingleStreamConnectionContext`
- `IHttpMultiplexConnectionContext`
- `IHttpStreamContext`
- `HttpStreamId`

This keeps HTTP/1.1 clean while allowing HTTP/2 and HTTP/3 to model concurrent logical streams explicitly.

## Suggested Interfaces

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpConnection : ITransportConnection
{
    HttpVersion Version { get; }
    bool IsSecure { get; }
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpSingleStreamConnection : IHttpConnection
{
    IHttpSingleStreamConnectionContext Open();

    ValueTask<IHttpSingleStreamConnectionContext> OpenAsync(
        CancellationToken cancellationToken = default);
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpMultiplexConnection : IHttpConnection
{
    IHttpMultiplexConnectionContext Open();

    ValueTask<IHttpMultiplexConnectionContext> OpenAsync(
        CancellationToken cancellationToken = default);
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpSingleStreamConnectionContext : ITransportConnectionContext
{
    IAsyncEnumerable<IHttpContext> ReceiveAsync(
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        IHttpContext context,
        CancellationToken cancellationToken = default);
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpMultiplexConnectionContext : ITransportConnectionContext
{
    IAsyncEnumerable<IHttpStreamContext> AcceptAsync(
        CancellationToken cancellationToken = default);
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public interface IHttpStreamContext : IAsyncDisposable
{
    HttpStreamId Id { get; }
    IHttpContext Context { get; }
    CancellationToken RequestAborted { get; }

    ValueTask SendAsync(CancellationToken cancellationToken = default);
    ValueTask AbortAsync(CancellationToken cancellationToken = default);
}
```

```csharp
namespace Assimalign.Cohesion.Http.Transports;

public readonly record struct HttpStreamId(long Value);
```

## Why This Shape Looks Strong

- `IHttpConnection` stays generic and reusable.
- `IHttpSingleStreamConnection` maps naturally to HTTP/1.1.
- `IHttpMultiplexConnection` maps naturally to HTTP/2 and HTTP/3.
- `IHttpStreamContext` makes per-stream response and abort semantics explicit.
- `HttpStreamId` avoids hard-coding `int` when HTTP/3 may want a wider identifier shape.

## Important Design Notes

### 1. The split should happen at both connection and context levels

If only the connection type is split, but the active context API remains `ReceiveAsync()` plus `SendAsync(IHttpContext)`, HTTP/2 and HTTP/3 still inherit a single-stream mental model.

### 2. HTTP/2 is still multiplexed at the HTTP layer

Even though HTTP/2 usually runs over one TCP stream, it is still multiplexed in terms of HTTP request and response streams. The HTTP abstraction should reflect that.

### 3. `IAsyncEnumerable` still fits well

Using `IAsyncEnumerable` for inbound work still makes sense. For multiplexed protocols it should yield stream contexts rather than raw HTTP contexts so stream lifecycle remains explicit.

### 4. Response sending should be stream-owned for multiplexed protocols

For HTTP/2 and HTTP/3, `SendAsync()` is more naturally a stream operation than a connection operation because responses are scoped to individual logical streams and may complete concurrently.

## What To Avoid

- making `IHttpConnection` mean HTTP/1.x only
- keeping one shared `IHttpConnectionContext` contract for all protocols
- forcing HTTP/2 and HTTP/3 to use the same response model as HTTP/1.1
- baking stream identifiers in as `int` too early

## Likely Migration Direction

If this design is adopted, the safest path is:

1. introduce the new interfaces beside the current ones
2. adapt HTTP/1.1 to `IHttpSingleStreamConnection`
3. adapt HTTP/2 and HTTP/3 to `IHttpMultiplexConnection`
4. deprecate the old shared connection-context shape once the new model is stable

## Open Questions

- Should `IHttpStreamContext` expose `IHttpContext` directly, or should request and response be surfaced separately?
- Should outbound stream creation exist now, or only when client-side HTTP transports are introduced?
- Should `IHttpMultiplexConnectionContext` also expose connection-level control operations such as graceful shutdown or GOAWAY?
- Should HTTP/1.1 pipelining remain modeled as `IAsyncEnumerable<IHttpContext>`, or should it eventually also move to a stream-like wrapper for symmetry?
