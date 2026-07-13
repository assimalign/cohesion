# Assimalign.Cohesion.Database.Hosting — Design

## Design intent

`DatabaseApplication` is the standalone host for the database engine resource. Per the
Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass
owning its own lifecycle in its own process; this project is that hosting shell,
composing the resource's units of work as hosted services and serving as the area's one
DI/Configuration/Logging seam.

This module also **owns the database server runtime** — the model-agnostic network
front-end (`DatabaseServer.Create`, the session pump). The server was originally a
separate `Database.Server` library; it was folded into this module by owner decision
on 2026-07-12 (see "The server runtime is a hosting concern" below). The server
*abstractions* (`IDatabaseServer`, `IDatabaseServerSession`, and the `ProtocolVersion`
value type) live in the area root — the hosting-isolation rule makes this module
unreferenceable by area libraries, so the seam must sit where they can see it. This
module ships the implementation and the composition surface only.

## Execution model

Threading is a per-service decision made by static dispatch from the execution menu
defined by `Assimalign.Cohesion.Hosting` (see
`libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `WriteAheadFlushService` | `DedicatedThreadService` (dedicated OS thread) | a synchronous blocking flush loop must own its thread for its whole life |
| `PageWriterService` | `DedicatedThreadService` (dedicated OS thread) | a synchronous blocking page write-back loop must own its thread for its whole life |
| `DatabaseServerHostService` (endpoint) | `BackgroundService` (pool-scheduled) | an async accept loop belongs on the pool |

Registration order is durability workers first, then any additional composed services,
then the endpoint. A host starts services in registration order and stops them in
reverse, so the endpoint starts last and stops first — connections drain ahead of the
durability workers.

## Execution-model mapping (the #902 worker inventory)

The plan for the database's background services, reconciling the Lane-H guardrails
(WAL flush / page writer on dedicated threads, endpoint on the pool) with requirement
R10 (engine self-sufficiency). **Core principle: the engine owns every
durability/maintenance work loop** behind the #902 `IDatabaseEngine` lifecycle seam,
so an embedded consumer gets identical behavior with no host at all;
`DatabaseApplicationOptions` merely *maps* each engine worker onto the execution menu
— the per-worker threading choice plus cadence knobs (group-commit window, checkpoint
interval, purge batch size). The host never owns the work, or embedded mode would
silently lose it.

| Worker | Owner | Execution-menu member | Rationale |
| --- | --- | --- | --- |
| WAL group-commit flusher | engine | `DedicatedThreadService` | Latency-critical steady loop — every commit waits on it, so it must be immune to thread-pool starvation. Drives the journal's durable-flush batching (`IStorageJournal.EnsureDurable` group commit). Today the engine flushes synchronously at commit; the async group-commit window is the #902 upgrade this slot maps. |
| Page write-back (dirty-page writer) | engine | `DedicatedThreadService` | Paced synchronous I/O that owns its thread for its whole life; coordinates with the buffer pool's `WriteAheadGate` (a page reaches the data file only once the journal is durable past its LSN). |
| Checkpointer (flush + journal truncate) | engine | `BackgroundService`, timer-driven | Periodic and not latency-critical; serializes with the flusher (checkpoint = durable flush, then truncate with continued LSNs). A timer loop on the pool is enough. |
| MVCC version purge / vacuum | engine | `BackgroundService` | Bursty, yieldy, low priority — drains aborted/pruneable version chains via `IVersionStore.PurgeWriterAsync` and the oldest-active prune bound. |
| B+Tree maintenance (tombstone vacuum, page merges) | engine (future) | `BackgroundService`, throttled | Same shape as version purge once the index layer grows compaction; throttled so maintenance never competes with foreground writes. |
| Protocol endpoint accept loop | hosting | `BackgroundService` | Already live (`DatabaseServerHostService`): an async accept loop belongs on the pool. Starts last, drains first. |
| Session idle sweep | — (server-internal) | not a host-service slot | Idle eviction is per-session `CancelAfter` timers inside the session pump; promoting it to a host service would add a slot with nothing to own. |
| Deadlock detection | — (lock manager, synchronous) | not a host-service slot (today) | The lock manager detects cycles at acquire time (requester-closes-cycle victim policy) — there is no background scanner to schedule. A wait-for-graph scanner only becomes a service if lock acquisition ever moves to blocking-with-timeout; noted as a possible future row, not planned. |

Until #902 lands the engine-owned worker seam, `WriteAheadFlushService` and
`PageWriterService` are the first two rows' *slots*: placeholders that park until
shutdown (see the next section). #902's acceptance criteria carry this same inventory.

## Why-this-not-that

### The server runtime is a hosting concern (2026-07-12 fold)

The wire-protocol server originally shipped as its own `Database.Server` project, and
the **resource hosting-isolation rule (COHRES002)** — the hosting module may reference
no same-area library except the area root — forced an awkward composition seam: this
module could not name `IDatabaseServer`, so the endpoint `BackgroundService` adapter
had to live *with the server* behind a public `DatabaseServer.CreateHostService(...)`
factory that the composition root wired into the host generically.

The 2026-07-12 owner decision resolves the tension the other way: **the server runtime
is part of the hosting concern** — it exists to put engines on the network, which is
precisely what the standalone host is for — so `Database.Server` was folded into this
module (mirroring the Web-area direction of merging `Web.Server` into `Web.Hosting`).
Consequences:

- **COHRES002 exemption (sanctioned, per `deviations.md` + `resource-areas.md`).** The
  merged module needs `Database.Protocol` (wire framing) and `Database.Security` (the
  authenticator seam) as direct same-area references. Both are the *server's own
  machinery*, not hosted features, and `Database.Protocol` cannot be aggregated into
  the area root because it references the root. The csproj therefore declares
  `CohesionHostingIsolationExemptions` for exactly those two assemblies.
  `Database.Execution` and `Database.Types` are deliberately **not** direct references
  — they arrive transitively through the sanctioned area-root reference
  (root → Execution → Types), which COHRES002 permits.
- **The cross-assembly endpoint seam is gone.** `DatabaseApplication` constructs the
  internal `DatabaseServerHostService` directly from
  `DatabaseApplicationOptions.Server`; `CreateHostService` was **internalized**
  (removed from the public surface). `DatabaseServer.Create(...)` stays public as the
  manual/custom composition path — a host that is not `DatabaseApplication` drives
  `IDatabaseServer.StartAsync`/`StopAsync` on its own lifecycle instead of wrapping an
  `IHostService`.
- **The server contract was promoted into the area root** (owner decision, later the
  same day): `IDatabaseServer`, `IDatabaseServerSession`, and the `ProtocolVersion`
  value type moved to `Assimalign.Cohesion.Database` — the Web area's shape
  (`IWebApplicationServer` lives in the `Web` root). COHRES001 makes this module
  unreferenceable by area libraries, so keeping the seam here would have made the
  server invisible to quotas/health (#167/#168) and a future `Database.Testing`
  factory. This module keeps the implementation (`DefaultDatabaseServer`, the session
  pump) and the composition surface (`DatabaseServer.Create`, `DatabaseServerOptions`).
  `ProtocolVersion.Current` stays with `Database.Protocol` as a static extension
  member — the version claim lives with the wire implementation that makes it true.

### One server for five engines

The server owns everything that is true regardless of data model — accept loop,
session limits, authentication handshake, frame pump, graceful drain — and delegates
everything model-specific to the engine session behind `IDatabaseSession`. The
alternative (a server per model) multiplies the security-critical surface with no
semantic gain. Transport comes from `libraries/Connections` (`IConnectionListener`),
so TCP/TLS/named-pipe/in-memory drivers are interchangeable; in-memory makes the
server testable without sockets.

### Composition seam

`DatabaseServer.Create(options)` — the options carry the engines list and a
**bound `IConnectionListener` instance**, not a listener factory: Connections
drivers bind at construction, so a factory would add a layer that defers nothing,
and passing the instance keeps ownership unambiguous — *the composition root creates
and disposes the listener; the server only accepts from it*. Stop is signaled by
cancelling the pending accept, never by disposing the listener.
`options.Authenticator` defaults to `DatabaseAuthenticator.AllowAll`
(`Database.Security`) — the MVP development posture, deliberately an explicit,
discoverable object rather than hidden server behavior.

### The model-agnostic execute path

The server receives statement *text* and tuple-codec parameter bytes off the
wire, but must never parse a model language (it references no `*.Language` or
model package). The bridge is the **text-execute seam on the root contract**:
`IDatabaseSession.ExecuteAsync(string, IReadOnlyDictionary<string, object?>?, CancellationToken)`
— each model's session translates text with its own parser (SQL:
`SqlQueryRequest.FromSql`). The rejected alternative — the server building typed
`QueryRequest`s — would couple the one shared front-end to every model's
language package. Parameters decode with `DatabaseValueCodec`
(`Database.Types`), one self-describing component per parameter; result rows
encode the same way, one component per column, so both directions ride the one
shared codec.

### Durability worker slots are documented placeholders, not owners of durability

Requirement R10 (the platform data layer) mandates **engine self-sufficiency**: an
engine owns its durability whether embedded or hosted, so the host composition is
composition-only. The SQL engine today flushes synchronously at commit (steal/no-force
WAL) and writes pages back inside its own storage layer, so there is **no host-driven
flush or page-writer work to do** — and there must not be, or an embedded consumer
(no host) would silently lose it. `WriteAheadFlushService`/`PageWriterService` are
therefore the execution-menu *slots* for a future engine-owned background
checkpoint/flush worker: they park until shutdown and are documented as such. When the
engine grows a host-mappable background-worker seam, these slots drive it. That engine
seam is filed as **#902** under the engine self-sufficiency feature #862; the full
worker inventory and per-worker menu assignments live in "Execution-model mapping"
above. The slots are on by default (to keep the standalone host's execution-menu
shape) and can be toggled off for embedded/self-sufficient composition.

### Engine lifecycle stays with the composition root

`IDatabaseEngine` carries no start/stop on its contract (the concrete engines expose
their own `StartAsync`/`StopAsync`). The host therefore serves engines the composition
root started; it exposes them on the context but does not drive their lifecycle. A
root-level engine-lifecycle seam is part of #902.

## Session state machine

`Connected → Startup received → Authenticating → Ready ⇄ Executing → Terminated`.
Guardrails baked into the options because they are DoS-critical (the HTTP/1.1 limits
lesson, #791): unauthenticated connections are dropped after `AuthenticationTimeout`;
`MaxSessions` bounds concurrency (rejections use the protocol `Unavailable` error);
idle sessions are evicted; `StopAsync` drains within `ShutdownDrainTimeout` then aborts.

Implementation decisions:

- **Version negotiation:** an unknown *major* in `Startup` earns
  `UnsupportedVersion` and a close; the server then speaks `ProtocolVersion.Current`
  (minors are additive by the protocol's contract, so no per-minor branching yet).
- **Database binding** resolves across the registered engines: already-open
  databases first (`TryGetDatabase`), then an open attempt per engine; no match →
  `DatabaseNotFound` and close.
- **Authenticate exchange (MVP):** the challenge frame carries no payload (the
  trust method); the client's response bytes pass to `IDatabaseAuthenticator`
  as opaque evidence. Method-specific payload schemas arrive with real
  authenticators.
- **`MaxSessions` counts handshaking sessions too** — an unauthenticated
  connection holds a slot, otherwise the cap would not bound resource use at
  all. Over-limit connections get the `Unavailable` error frame immediately at
  accept and never become sessions.
- **Error taxonomy per exchange:** statement-level failures keep the session in
  Ready — `DatabaseParseException` → `ParseFailure`, any other
  `DatabaseException` → `ExecutionFailure` (an execution error is not a protocol
  violation). Framing/order violations (`ProtocolException`, malformed parameter
  components) → `ProtocolViolation` **and close**; anything unexpected →
  `Internal` and close.
- **Two-phase stop:** a *soft stop* token ends the accept loop and cancels reads
  at frame boundaries (idle sessions close immediately, telling the peer
  `Unavailable`); in-flight executions run on the session lifetime token and get
  the full drain budget. When the budget lapses, the *hard abort* token cancels
  executions and aborts connections. Session pumps own their errors — their
  completion tasks never fault, so drain is a plain `WhenAll`.

## Configuration conventions

`DatabaseHostConfiguration.FromEnvironment()` binds the environment-variable
conventions a gateway injects when it launches the host — `COHESION_DATABASE_DATA_PATH`,
`COHESION_DATABASE_ENDPOINT_PORT`, `COHESION_DATABASE_DURABILITY`. Binding lives here
because the hosting module is the area's one Configuration seam; the bound values shape
how the composition root builds the engine (data path, durability) and the listener
(port). The `Database.ApplicationModel` resource sets the same variable names on its
realized process, so the manifest side and the host side agree by convention (the two
projects share no assembly).

## Status and non-goals

- No builder or DI container surface yet; construct `DatabaseApplication` with
  `DatabaseApplicationOptions` directly. A `CreateBuilder` surface can follow the
  `WebApplication` pattern when the resource matures.
- No governance/quotas (#167) or health/readiness (#168) surfaces yet — separate
  features.
- No per-model message handling in the server — payload semantics belong to engines
  and per-model clients.
- No connection-level replication endpoints in the MVP (replication transport rides
  its own feature); no transaction frames yet (explicit transaction control over the
  wire lands with the protocol's `Transaction` payload schema).
- No HTTP admin surface — that is the root `Database` project's private-Web concern,
  deliberately separate from the wire protocol path.
- Direct references: the area root, the COHRES002-exempted `Database.Protocol` +
  `Database.Security` server machinery, and the non-area `Connections` + `Hosting`
  foundations. Nothing else.

## AOT posture

Static composition: the composition root hands the server its engine list; nothing is
discovered at runtime. Value encoding is the shared runtime-type switch
(`DatabaseValueCodec`) — no reflection.
