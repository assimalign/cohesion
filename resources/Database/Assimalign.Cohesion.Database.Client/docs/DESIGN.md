# Assimalign.Cohesion.Database.Client — Design

The client half of the #852 spine (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md)): one model-agnostic protocol
client the five per-model clients wrap, exactly mirroring the one
model-agnostic server. Model semantics live in the statement text and the
per-model packages; this project owns everything that is true for all of them —
dialing, handshake, execute exchange, value encoding, pooling, error mapping.

## Why-this-not-that decisions

- **Text in, materialized values out.** The connection executes statement
  *text* plus boxed parameters — the same shape the wire carries and the same
  seam the server calls on `IDatabaseSession`. Typed request/result surfaces
  (SQL command objects, document APIs) belong to the per-model clients that
  know their language; putting any of them here would make the shared core
  model-aware.
- **Results materialize (MVP).** The wire streams `ResultHeader → ResultRow* →
  ResultComplete`, but a pooled connection is only reusable once its exchange is
  fully drained — handing the application an unfinished stream would couple row
  consumption to pool health (the classic leaked-reader bug). Buffering rows
  keeps pooling correct and simple; an incremental surface can be added by the
  per-model clients once cursors/paging give it real semantics.
- **Pooling reuses the authenticated session.** A pooled connection returns to
  an idle stack with its wire session still in the ready state; the next rent
  skips dial + handshake entirely (the acceptance criterion behind the pool). A
  slot semaphore bounds total connections at `MaxPoolSize`; exhausted rents wait
  for a return rather than failing. Health at return decides reuse: only
  statement-level errors (`ParseFailure`/`ExecutionFailure`) leave a connection
  poolable — every other error frame, transport fault, or mid-exchange close
  marks it broken and it is closed instead of pooled. Known limitation: a
  server-side eviction (idle timeout) of an *idle pooled* connection is
  discovered at next use, not at return — a rent-time liveness ping is future
  hardening, not MVP.
- **Connection string carries identity, never the driver.** `key=value;` parsing
  covers `Database`, `Principal`, `Endpoint=host[:port]` (→ `DnsEndPoint`), and
  `MaxPoolSize`. The transport factory is a typed option
  (`DatabaseClientOptions.ConnectionFactory`) because drivers are composed
  statically for AOT — a driver name in a string implies runtime plugin
  loading, which the platform forbids. Non-network endpoints (the in-memory
  transport's named endpoints) cannot be expressed as strings at all; the typed
  `EndPoint` property is the escape hatch, and it is how tests compose against
  `Connections.InMemory`.
- **Errors carry the wire code.** `DatabaseClientException : DatabaseException`
  exposes the stable `ProtocolErrorCode` so callers program against the wire
  contract, not message text. Handshake rejections (unsupported version,
  unknown database, capacity, failed authentication) surface the server's code
  verbatim from the error frame.

## Lifecycle pattern

`DatabaseClient.Create` performs no I/O. Rent → open-or-reuse → execute →
dispose-returns-to-pool. Disposing the client closes all idle connections;
connections still rented at that moment close for real when they return. Real
closure sends a best-effort `Terminate` frame before transport teardown.

## AOT posture

No reflection: parameter and row values go through `DatabaseValueCodec`'s
runtime-type switch, results are boxed scalars, and transports are composed
statically.

## Non-goals

- No per-model APIs, no LINQ, no ORM surface — per-model clients build here.
- No client-side statement parsing or validation; the server's session owns its
  language.
- No transaction frames yet (the wire's `Transaction` message lands with the
  protocol's transaction payload schema).
- No TLS logic — security layers compose in the connection factory
  (`Connections.Security`).
