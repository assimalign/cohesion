# Assimalign.Cohesion.Database.Hosting — Design

## Design intent

`DatabaseApplication` is the standalone host for the database engine resource. Per the
Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass
owning its own lifecycle in its own process; this project is that hosting shell,
composing the resource's units of work as hosted services and serving as the area's one
DI/Configuration/Logging seam.

This module also **owns the database server runtime** — the model-agnostic network
front-end (`IDatabaseServer`, `DatabaseServer.Create`, the session pump). The server
was originally a separate `Database.Server` library; it was folded into this module by
owner decision on 2026-07-12 (see "The server runtime is a hosting concern" below).

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
- The earlier alternative — promoting the server contract into the area root, the Web
  area's shape (`IWebApplicationServer` lives in the `Web` root) — remains possible
  but is no longer needed: the host owns the endpoint directly.

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
seam is filed as **#902** under the engine self-sufficiency feature #862. The slots
are on by default (to keep the standalone host's execution-menu shape) and can be
toggled off for embedded/self-sufficient composition.

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
