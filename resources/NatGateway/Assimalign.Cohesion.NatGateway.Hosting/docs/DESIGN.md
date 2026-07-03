# Assimalign.Cohesion.NatGateway.Hosting Design

## Design Intent

`NatGatewayApplication` is the standalone host for the NAT gateway resource. Per the Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass owning its own lifecycle in its own process; this project is that hosting shell, composing the resource's units of work as hosted services.

## Execution model

Threading is a per-service decision made by static dispatch from the execution menu defined by `Assimalign.Cohesion.Hosting` (see `libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `TranslationDataPlaneService` | `IHostService` directly (owns one thread per core) | per-core loops to translate and forward packets |

The data plane owns one thread per core for its entire life - the menu's third member: the component implements `IHostService` directly, spinning up its per-core loops on start and joining them within the shutdown budget on stop.

## Status and non-goals

- This is a scaffold: service bodies are placeholders that park until the host stops, so the application starts and drains cleanly today. The real loops land with the resource implementation.
- No builder or DI surface yet; construct `NatGatewayApplication` with `NatGatewayApplicationOptions` directly. A `CreateBuilder` surface can follow the `WebApplication` pattern when the resource matures.
- The project deliberately references only `Assimalign.Cohesion.Hosting` until the resource library's contracts are ready to wire in.