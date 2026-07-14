# Assimalign.Cohesion.Database.Server — Design

The shared server core: the guided base per-model database servers derive from.
This document carries the server state-machine design record (it moves with the
machinery — third move) and the **extraction evidence** the area DESIGN's
decision log demanded.

## The extraction record (2026-07-14 — the second model server)

**The trigger, as recorded** (area DESIGN decision log, 2026-07-14 row): the
server machinery was deliberately folded into `Database.Sql` as internals —
a shared base at n=1 was premature abstraction — with the instruction that
*"when the second model server is built, extract the then-proven common core —
predicted: session table + guardrails + two-phase drain, NOT the execute pump —
into a shared library, with the second implementation as evidence."*

**The evidence: building `KeyValueDatabaseServer` proved the prediction
directionally right and quantitatively short.** What actually proved common:

| Machinery | Predicted common? | Proved common? | Why |
|---|---|---|---|
| Session table + `MaxSessions` rejection | yes | **yes** | model-free bookkeeping |
| Auth-timeout / idle-eviction guardrails | yes | **yes** | model-free timers over the frame reader |
| Two-phase drain (soft stop → budget → hard abort) | yes | **yes** | model-free lifecycle |
| Handshake (version negotiation, database binding, authenticate exchange) | implicitly | **yes** | binds through `IDatabaseEngine`/`IDatabaseAuthenticator` only |
| **The execute pump** (Execute decode → text seam → result framing) | **no** | **yes** | the second model rides the root's text-execute seam: its command grammar travels the existing Execute message and its results ride the generic ResultHeader/Row/Complete framing — the pump never learns the model |

The prediction expected "KV wants binary command paths, not statement text."
The KV bring-up took the opposite, cheaper path (the model's `docs/COMMANDS.md`
decision): a five-verb text grammar over named tuple-codec parameters, which is
exactly as binary where it matters (keys/values are `Binary` components) with
zero protocol changes. Consequence: **the extraction scope follows the
evidence** — this library carries the whole machinery, pump included, and the
model derivations are ~30-line types (engine + options naming). The
model-specific seam predicted by the fold (custom frame handling) was
deliberately **not** pre-built: no virtual pump hooks exist, because no second
implementation demanded one. A model that someday needs custom framing adds the
seam with its own evidence (the same discipline that governed this extraction).

One evidence-driven adjustment was folded in: the result-set framing writes
`QueryResultSet.AffectedCount` into `ResultComplete` instead of a hardcoded
`-1` — the key-value model's one-row outcome sets carry real affected counts
(1/0), and SQL's materialized sets still report `-1`, so SQL wire behavior is
unchanged.

**Placement history (all recorded):** folded into `Database.Hosting`
(2026-07-12) → resurrected as the shared `Database.Server` base (2026-07-13) →
folded into `Database.Sql` as internals (2026-07-14, n=1) → **extracted here
(2026-07-14, n=2, evidence-based)**. The name returns; the difference from the
2026-07-13 resurrection is that this time two model servers prove what the
library contains.

## Composition seam

A model server derives from `DatabaseServer`, passing its one engine and its
`DatabaseServerOptions` subtype to the protected constructor; the base
validates (bound listener required, positive session limit) and implements the
root's `IDatabaseServer`/`IDatabaseServerContext`. The options carry a **bound
`IConnectionListener` instance**, not a factory: Connections drivers bind at
construction, and passing the instance keeps ownership unambiguous — *the
composition root creates and disposes the listener; the server only accepts
from it*. `Authenticator` defaults to `DatabaseAuthenticator.AllowAll` (the MVP
development posture, deliberately an explicit object rather than hidden
behavior). Engines are data machines owned by the composition root; the server
never disposes its engine.

## The text-execute path

The pump receives statement *text* and tuple-codec parameter bytes off the
wire; the bridge to the engine is the **text-execute seam on the root
contract** — `IDatabaseSession.ExecuteAsync(string, parameters, ct)` — which
each model's session implements with its own statement surface
(`SqlQueryRequest.FromSql` for SQL; the command-grammar parser for key-value).
Parameters decode with `DatabaseValueCodec` (one self-describing component per
parameter); result rows encode the same way — both directions ride the one
shared codec the client core also speaks. The server never parses any model
language: that is the invariant that made the pump extractable.

## Session state machine

`Connected → Startup received → Authenticating → Ready ⇄ Executing → Terminated`.
Guardrails baked into the options because they are DoS-critical (the HTTP/1.1
limits lesson, #791): unauthenticated connections are dropped after
`AuthenticationTimeout`; `MaxSessions` bounds concurrency (rejections use the
protocol `Unavailable` error); idle sessions are evicted; `StopAsync` drains
within `ShutdownDrainTimeout` then aborts.

Implementation decisions (carried from the machinery's prior homes — this
record moves with the machinery):

- **Version negotiation:** an unknown *major* in `Startup` earns
  `UnsupportedVersion` and a close; the server then speaks
  `ProtocolVersion.Current` (minors are additive by the protocol's contract, so
  no per-minor branching yet).
- **Database binding** resolves on the server's one engine: already-open
  databases first (`TryGetDatabase`), then an open attempt; no match →
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
  `DatabaseException` → `ExecutionFailure`. Framing/order violations
  (`ProtocolException`, malformed parameter components) → `ProtocolViolation`
  **and close**; anything unexpected → `Internal` and close. A child-root
  exception that escapes raw reaches the wire as `Internal` and closes the
  session — the engine's model boundary is where wrapping into
  `DatabaseException` belongs.
- **Two-phase stop:** a *soft stop* token ends the accept loop and cancels reads
  at frame boundaries (idle sessions close immediately, telling the peer
  `Unavailable`); in-flight executions run on the session lifetime token and get
  the full drain budget. When the budget lapses, the *hard abort* token cancels
  executions and aborts connections. Session pumps own their errors — their
  completion tasks never fault, so drain is a plain `WhenAll`.

## Error model

The server layer defines no exception root of its own: wire failures are the
protocol's (`ProtocolException`, mapped to wire error codes as above), and
engine failures are the area root's (`DatabaseException` family). Configuration
misuse (no listener, non-positive session limit, null engine) throws argument
exceptions at construction.

## Testing posture

The suite in `tests/` drives the machinery through a **fake model engine**
(`TestObjects/FakeModelEngine`) — no SQL, no key-value — which is the
model-independence proof in executable form. Model-specific wire behaviors
(SQL statements, the KV command grammar and outcome shapes) stay in the model
suites, over their real engines.

## Non-goals

No host-service adapter (`Database.Hosting` wraps `IDatabaseServer` generically
through the root seam); no connection-level replication endpoints; no
transaction frames yet (explicit transaction control over the wire lands with
the protocol's `Transaction` payload schema); no TLS/transport policy —
transports come bound from `libraries/Connections` drivers, and the composition
root owns them; no speculative model-specific pump seams (see the extraction
record).

## AOT posture

Hand-encoded protocol frames, span-based codecs, no reflection, no runtime
codegen.
