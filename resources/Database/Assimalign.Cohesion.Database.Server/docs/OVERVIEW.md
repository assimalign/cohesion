# Assimalign.Cohesion.Database.Server — Overview

The model-agnostic network front-end for database hosts: accepts connections over `libraries/Connections` drivers, authenticates a session principal, binds the session to a database, and pumps wire-protocol frames into engine `IDatabaseSession` executions.

## Scope

- `IDatabaseServer` — accept/drain lifecycle over the registered engines; created
  with `DatabaseServer.Create(options)`
- `IDatabaseServerSession` — connection + principal + engine-session binding
- `DatabaseServerOptions` — composition (engines, bound listener, authenticator)
  plus the DoS guardrails: session limit, auth/idle timeouts, drain budget

Statements execute through the root contract's text-execute seam
(`IDatabaseSession.ExecuteAsync(string, parameters)`), so the server never
parses any model language; parameters and result rows ride the shared
tuple-codec (`DatabaseValueCodec` in `Database.Types`).

## Dependencies

- `Assimalign.Cohesion.Database` / `Database.Execution` (engine and execution contracts)
- `Assimalign.Cohesion.Database.Protocol` (framing)
- `Assimalign.Cohesion.Database.Security` (the authenticator seam)
- `Assimalign.Cohesion.Database.Types` (the shared value codec for wire parameters and rows)
- `Assimalign.Cohesion.Connections` (transport drivers)

## Consumers

`Database.Hosting` composes and starts the server inside its endpoint `BackgroundService`; `Database.Client` is the counterpart on the other end of the wire.
