# Assimalign.Cohesion.Database.Server — Overview

The model-agnostic network front-end for database hosts: accepts connections over `libraries/Connections` drivers, authenticates a session principal, binds the session to a database, and pumps wire-protocol frames into engine `IDatabaseSession` executions.

## Scope

- `IDatabaseServer` — accept/drain lifecycle over the registered engines
- `IDatabaseServerSession` — connection + principal + engine-session binding
- `DatabaseServerOptions` — session limits, auth/idle timeouts, drain budget

## Dependencies

- `Assimalign.Cohesion.Database` / `Database.Execution` (engine and execution contracts)
- `Assimalign.Cohesion.Database.Protocol` (framing)
- `Assimalign.Cohesion.Database.Security` (authorization seam)
- `Assimalign.Cohesion.Connections` (transport drivers)

## Consumers

`Database.Hosting` composes and starts the server inside its endpoint `BackgroundService`; `Database.Client` is the counterpart on the other end of the wire.
