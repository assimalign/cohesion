# Assimalign.Cohesion.Database.Server — Design

## Intent

One server for five engines. The server owns everything that is true regardless of data model — accept loop, session limits, authentication handshake, frame pump, graceful drain — and delegates everything model-specific to the engine session behind `IDatabaseSession`. The alternative (a server per model) multiplies the security-critical surface with no semantic gain.

## Placement

- The server is a *library* the host composes, not a host service itself: `Database.Hosting`'s `QueryEndpointService` (a `BackgroundService`, per the Hosting execution menu) runs the accept loop. This mirrors the Web split (`WebApplicationServer` inside `Web.Hosting`).
- Transport comes from `libraries/Connections` (`IConnectionListener`), so TCP/TLS/named-pipe/in-memory drivers are interchangeable; in-memory makes the server testable without sockets.
- The relocated `IDatabaseServer` contract lives here, not in the contract root — the root stub (`int Version`, with a stray Web using) was removed in the scaffold pass; server concerns don't belong in the engine contract root.

## Session state machine

`Connected → Startup received → Authenticating → Ready ↔ Executing → Terminated`. Guardrails baked into the options because they are DoS-critical (the HTTP/1.1 limits lesson, #791): unauthenticated connections are dropped after `AuthenticationTimeout`; `MaxSessions` bounds concurrency (rejections use the protocol `Unavailable` error); idle sessions are evicted; `StopAsync` drains within `ShutdownDrainTimeout` then aborts.

## Non-goals

- No per-model message handling here — payload semantics belong to engines and per-model clients.
- No connection-level replication endpoints in the MVP (replication transport rides its own feature).
- No HTTP admin surface — that is the root `Database` project's private-Web concern, deliberately separate from the wire protocol path.

## AOT posture

Static composition: the host hands the server its engine list; nothing is discovered at runtime.
