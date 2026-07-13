# Assimalign.Cohesion.Database.Server — Design

## Design intent

One shared implementation of everything a database wire-protocol server does
regardless of data model, packaged as a **guided abstract base**
(`DatabaseServer`) that per-model servers derive from. Servers are per-model by
design (2026-07-13 owner decision): each fronts exactly one engine, so
model-specific wire behavior (typed relational payloads, per-model transaction
frames, model command verbs) has a natural home in the derived type
(`SqlDatabaseServer`, and each future model's server), while the security-critical
machinery — accept loop, session state machine, guardrails, drain — exists once,
here.

## Why this library exists (the half-unwind of the 2026-07-12 fold)

`Database.Server` originally shipped as its own project, was **folded into
`Database.Hosting`** on 2026-07-12 (the server runtime was judged a hosting
concern, mirroring the Web area's `Web.Server`→`Web.Hosting` direction), and was
**resurrected here on 2026-07-13** — a deliberate, recorded reversal of part of
that fold. The new information that changed the calculus: the approved design
made servers **per-model**, and per-model servers must *derive from* the shared
base — but the hosting-isolation rule (COHRES001) forbids any area library from
referencing `Database.Hosting`, so a base class living in the hosting module is
underivable by exactly the packages that need it (`Database.Sql`, and every
future model). The 2026-07-12 fold was reasoned over a *model-agnostic singleton*
server that only the host composed; once the server became a per-model family
member, the machinery had to sit where models can see it: above the root, below
the models. What the fold got right is retained: `Database.Hosting` remains the
place servers are *composed into a host process* — it wraps any `IDatabaseServer`
as an endpoint host service — it just no longer owns the server implementation.

Dependency shape: root + `Connections` only. `Database.Protocol` (framing) and
`Database.Security` (the authenticator seam) arrive transitively through the
root's child-root rollup. Model packages reference this library
(feature → feature, COHRES-legal); `Database.Hosting` does **not** reference it —
the hosting module composes `IDatabaseServer` through the root seam alone.

## The guided base shape

`DatabaseServer` implements `IDatabaseServer` with a `protected` constructor
taking the one engine plus `DatabaseServerOptions` — the derived server supplies
the engine (this replaces the old `DatabaseServerOptions.Engines` list;
per-model servers made a list wrong by construction). The base is concrete
machinery with no abstract members today: deriving is what gives a model server
its identity and its extension point — model-specific wire behavior will grow as
`protected virtual` hooks on the base when the protocol's model-specific surface
lands (per-model message families, transaction frames), which is why the base is
abstract rather than sealed-with-a-factory. `IDatabaseServerContext` is
implemented internally (engine + live session snapshot).

## Composition seam

The derived server's factory (`SqlDatabaseServer.Create(engine, options)`) or
the model's builder verb (`AddSqlServer(engine, configure)`) composes a server.
The options carry a **bound `IConnectionListener` instance**, not a listener
factory: Connections drivers bind at construction, so a factory would add a
layer that defers nothing, and passing the instance keeps ownership unambiguous —
*the composition root creates and disposes the listener; the server only accepts
from it*. Stop is signaled by cancelling the pending accept, never by disposing
the listener. `options.Authenticator` defaults to
`DatabaseAuthenticator.AllowAll` (`Database.Security`) — the MVP development
posture, deliberately an explicit, discoverable object rather than hidden server
behavior. The engine is likewise owned by the composition root: engines are data
machines (create → use → dispose), and the server never disposes its engine.

## The model-agnostic execute path

The server receives statement *text* and tuple-codec parameter bytes off the
wire, but never parses a model language (this library references no `*.Language`
or model package). The bridge is the **text-execute seam on the root contract**:
`IDatabaseSession.ExecuteAsync(string, IReadOnlyDictionary<string, object?>?, CancellationToken)`
— each model's session translates text with its own parser (SQL:
`SqlQueryRequest.FromSql`). The rejected alternative — the server building typed
`QueryRequest`s — would couple the shared machinery to every model's language
package. Parameters decode with `DatabaseValueCodec` (`Database.Types`), one
self-describing component per parameter; result rows encode the same way, one
component per column, so both directions ride the one shared codec.

## Session state machine

`Connected → Startup received → Authenticating → Ready ⇄ Executing → Terminated`.
Guardrails baked into the options because they are DoS-critical (the HTTP/1.1
limits lesson, #791): unauthenticated connections are dropped after
`AuthenticationTimeout`; `MaxSessions` bounds concurrency (rejections use the
protocol `Unavailable` error); idle sessions are evicted; `StopAsync` drains
within `ShutdownDrainTimeout` then aborts.

Implementation decisions (carried over from the server's prior homes — this
record moved with the machinery):

- **Version negotiation:** an unknown *major* in `Startup` earns
  `UnsupportedVersion` and a close; the server then speaks
  `ProtocolVersion.Current` (minors are additive by the protocol's contract, so
  no per-minor branching yet).
- **Database binding** resolves on the server's one engine: already-open
  databases first (`TryGetDatabase`), then an open attempt; no match →
  `DatabaseNotFound` and close. (The pre-per-model server probed a *list* of
  engines in registration order; one engine per server removed that ambiguity.)
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
  `Internal` and close. A child-root exception that escapes raw (for example a
  `StorageException` the engine failed to wrap) reaches the wire as `Internal`
  and closes the session — the engine's model boundary is where wrapping into
  `DatabaseException` belongs.
- **Two-phase stop:** a *soft stop* token ends the accept loop and cancels reads
  at frame boundaries (idle sessions close immediately, telling the peer
  `Unavailable`); in-flight executions run on the session lifetime token and get
  the full drain budget. When the budget lapses, the *hard abort* token cancels
  executions and aborts connections. Session pumps own their errors — their
  completion tasks never fault, so drain is a plain `WhenAll`.

## Error model

This library defines no exception root of its own: wire failures are the
protocol's (`ProtocolException`, mapped to wire error codes as above), and
engine failures are the area root's (`DatabaseException` family). Configuration
misuse (no listener, non-positive session limit, null engine) throws argument
exceptions at construction.

## AOT posture

Static composition: the derived server hands the base its engine; nothing is
discovered at runtime. Value encoding is the shared runtime-type switch
(`DatabaseValueCodec`) — no reflection.

## Non-goals

- No host-service adapter — `Database.Hosting` wraps `IDatabaseServer`
  generically; this library knows nothing about the hosting model.
- No per-model message handling in the base — model payload semantics belong to
  the derived servers and per-model clients (the reason the family is per-model
  at all).
- No connection-level replication endpoints, and no transaction frames yet
  (explicit transaction control over the wire lands with the protocol's
  `Transaction` payload schema).
- No TLS/transport policy — transports come bound from `libraries/Connections`
  drivers; the composition root owns them.
