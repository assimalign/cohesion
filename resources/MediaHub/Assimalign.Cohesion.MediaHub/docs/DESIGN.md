# Assimalign.Cohesion.MediaHub Design

## Design intent

The area root owns only the contracts that feature packages compose against. `IMediaHubApplicationBuilder` is the contract-only builder seam, while `IMediaHubApplication` supplies the hosting-free application lifecycle.

## Hosting isolation

The root contracts are hosting-free (O34): `IMediaHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMediaHubApplicationContext` exposes `ContentRootPath`. `IMediaHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

The concrete application, builder, and context live in `Assimalign.Cohesion.MediaHub.Hosting`; options and supporting services remain internal.

## Composition lifecycle

Background-work registration belongs to the concrete `MediaHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<MediaHubApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

Enabled resources retain their ambient context and registered control-plane behavior; plain application defaults are described by the Hosting design.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.

## Hosting-free application contract (O34)

`MediaHubApplication.CreateBuilder(args)` returns the public concrete `MediaHubApplicationBuilder`; its `Build()` returns the public `MediaHubApplication : Host<MediaHubApplicationContext>`. The public `MediaHubApplicationContext` implements `IMediaHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
