# Assimalign.Cohesion.EventHub.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. `EventHubApplication.CreateBuilder(args)` returns the public concrete `EventHubApplicationBuilder`; its `Build()` returns the public `EventHubApplication : Host<EventHubApplicationContext>`. The public `EventHubApplicationContext` implements `IEventHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Filler execution model

The built host exposes the ordered `HostedServices` materialized from explicit builder registrations and a production `HostEnvironment`; the collection remains empty when nothing is registered. Instance and factory registrations share one order, factories run exactly once per `Build()` after the context exists, and a null factory result fails the build. Services start in registration order and stop in reverse registration order through the shared host lifecycle without claiming that event-hub behavior exists.

`PartitionFlushService` and `IngressEndpointService` remain as dormant future service stubs. The filler builder does not register them automatically.

## Boundaries

The module references the area root, DependencyInjection, and Hosting publicly. Its Web, Web.Hosting, HTTP, and transport implementation dependencies are private, with their resolved closure supplied by the area runtime framework. It references no application-model library: the resource integration, `EventHub.Hosting.ApplicationModel`, references this module instead. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

## Resource integration

The area's resource integration is `Assimalign.Cohesion.EventHub.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library.

## Concrete composition (T10 / O34)

Background-work registration belongs to the concrete `EventHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<EventHubApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

## Dependency injection composition

`EventHubApplicationBuilder.Services` is the application's one composition registry, created with `EnableDynamicCode = false`, `ValidateOnBuild = true`, and `ValidateScopes = true`. The module registers only factories and instances, so the provider stays reflection-free. Every lifecycle service is an `IHostService` registration: `AddService(IHostService)` registers the caller's instance, `AddService(Func<EventHubApplicationContext, IHostService>)` registers a factory closed over the context, and a direct `Services.AddSingleton<IHostService>(...)` joins the lifecycle the same way. Order is registration order.

`Build` closes registration (the container becomes read-only, and a second `Build` throws), creates the provider once, and resolves `IEnumerable<IHostService>` once into the snapshot the host reuses for every start, stop, and rollback pass. The application owns the provider: disposal stops the host, then disposes the provider, which disposes every factory-created service in reverse creation order while instance registrations stay with their callers. A failed `Build` disposes the provider before rethrowing. The concrete context exposes the provider as `ServiceProvider` to hosting-layer factories; the area root and its features never see it.

