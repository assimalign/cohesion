# Assimalign.Cohesion.MediaHub.Hosting Design

## Design Intent

`MediaHubApplication` is the standalone host for the media hub resource. Per the Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass owning its own lifecycle in its own process; this project is that hosting shell, composing the resource's units of work as hosted services.

## Execution model

Threading is a per-service decision made by static dispatch from the execution menu defined by `Assimalign.Cohesion.Hosting` (see `libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `ContentIoService` | `DedicatedThreadService` (dedicated OS thread) | blocking loop to read and write media content with blocking file I/O |
| `StreamingEndpointService` | `BackgroundService` (pool-scheduled) | async loop to serve streaming sessions |

The engine splits along the blocking/async seam: durability work is synchronous blocking I/O that must own its thread for its whole life (`DedicatedThreadService`), while the endpoint is an async accept loop that belongs on the pool (`BackgroundService`).

## Status and non-goals

- This is a scaffold: service bodies are placeholders that park until the host stops, so the application starts and drains cleanly today. The real loops land with the resource implementation.
- No builder or DI surface yet; construct `MediaHubApplication` with `MediaHubApplicationOptions` directly. A `CreateBuilder` surface can follow the `WebApplication` pattern when the resource matures.
- The project deliberately references only `Assimalign.Cohesion.Hosting` until the resource library's contracts are ready to wire in.