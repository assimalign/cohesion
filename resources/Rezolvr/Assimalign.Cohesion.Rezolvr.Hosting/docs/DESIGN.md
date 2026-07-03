# Assimalign.Cohesion.Rezolvr.Hosting Design

## Design Intent

`RezolvrApplication` is the standalone host for the name resolver resource. Per the Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass owning its own lifecycle in its own process; this project is that hosting shell, composing the resource's units of work as hosted services.

## Execution model

Threading is a per-service decision made by static dispatch from the execution menu defined by `Assimalign.Cohesion.Hosting` (see `libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `ResolverEndpointService` | `BackgroundService` (pool-scheduled) | async loop to answer resolution queries |

The name resolver is asynchronous I/O end to end: its loops spend their lives awaiting sockets and queues, so pooled `BackgroundService` is the whole composition - a dedicated thread would sit idle between requests.

## Status and non-goals

- This is a scaffold: service bodies are placeholders that park until the host stops, so the application starts and drains cleanly today. The real loops land with the resource implementation.
- No builder or DI surface yet; construct `RezolvrApplication` with `RezolvrApplicationOptions` directly. A `CreateBuilder` surface can follow the `WebApplication` pattern when the resource matures.
- The project deliberately references only `Assimalign.Cohesion.Hosting` until the resource library's contracts are ready to wire in.