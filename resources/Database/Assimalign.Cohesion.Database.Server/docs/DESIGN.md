# Assimalign.Cohesion.Database.Server — Design

## Intent

One server for five engines. The server owns everything that is true regardless of data model — accept loop, session limits, authentication handshake, frame pump, graceful drain — and delegates everything model-specific to the engine session behind `IDatabaseSession`. The alternative (a server per model) multiplies the security-critical surface with no semantic gain.

## Placement

- The server is a *library* a host composes. The endpoint host service — the
  `BackgroundService` that runs the accept loop and drains it on shutdown — lives
  **here**, as `DatabaseServerHostService` (obtained via `DatabaseServer.CreateHostService`),
  not in `Database.Hosting`. The resource hosting-isolation rule (COHRES002) bars the
  hosting module from referencing any same-area library except the area root, so the
  module cannot name `IDatabaseServer` to compose it; the adapter therefore lives with
  the server, which depends on the non-area hosting foundation (`Assimalign.Cohesion.Hosting`)
  for the `BackgroundService` base. `Database.Hosting` composes the returned
  `IHostService` generically. (This differs from the Web split, where the server
  contract `IWebApplicationServer` lives in the `Web` root so `Web.Hosting` can own the
  server; promoting the database server contract into the root is a deferred option.)
- Transport comes from `libraries/Connections` (`IConnectionListener`), so TCP/TLS/named-pipe/in-memory drivers are interchangeable; in-memory makes the server testable without sockets.
- The `IDatabaseServer` contract lives here, not in the contract root — the root stub (`int Version`, with a stray Web using) was removed in the scaffold pass; server concerns don't belong in the engine contract root.

## Composition seam

`DatabaseServer.Create(options)` — the options carry the engines list and a
**bound `IConnectionListener` instance**, not a listener factory: Connections
drivers bind at construction, so a factory would add a layer that defers nothing,
and passing the instance keeps ownership unambiguous — *the host composes and
disposes the listener; the server only accepts from it*. Stop is signaled by
cancelling the pending accept, never by disposing the host's listener.
`options.Authenticator` defaults to `DatabaseAuthenticator.AllowAll`
(`Database.Security`) — the MVP development posture, deliberately an explicit,
discoverable object rather than hidden server behavior.

## The model-agnostic execute path

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

## Session state machine

`Connected → Startup received → Authenticating → Ready ⇄ Executing → Terminated`. Guardrails baked into the options because they are DoS-critical (the HTTP/1.1 limits lesson, #791): unauthenticated connections are dropped after `AuthenticationTimeout`; `MaxSessions` bounds concurrency (rejections use the protocol `Unavailable` error); idle sessions are evicted; `StopAsync` drains within `ShutdownDrainTimeout` then aborts.

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

## Non-goals

- No per-model message handling here — payload semantics belong to engines and per-model clients.
- No connection-level replication endpoints in the MVP (replication transport rides its own feature).
- No HTTP admin surface — that is the root `Database` project's private-Web concern, deliberately separate from the wire protocol path.
- No transaction frames yet — explicit transaction control over the wire lands with the protocol's `Transaction` payload schema.

## AOT posture

Static composition: the host hands the server its engine list; nothing is discovered at runtime. Value encoding is the shared runtime-type switch (`DatabaseValueCodec`) — no reflection.
