# Assimalign.Cohesion.ConfigurationStore.Hosting

## Summary

Provides the public `ConfigurationStoreApplication.CreateBuilder(args)` entry point and the public
concrete durable ConfigurationStore host, builder, and context.

## Current Evaluation

- Status: code-first namespaces, durable JSON storage, HTTP protocol
- Runtime boundary: ConfigurationStore root plus shared Hosting and private Web transport

## Primary Responsibilities

- Capture `AddNamespace` declarations as first-start seeds.
- Persist configuration as plain JSON beneath the `data` directory using atomic replacement.
- Serve list and read operations plus standard health and endpoints routes.

The protocol listener starts after user services and drains before them. The host uses
`--endpoint` or the loopback default.
`/cohesion/v1` accepts only transport peers at `127.0.0.1`, `::1`,
or IPv4-mapped `127.0.0.1`; other peers receive 403, regardless of forwarded headers.
Bare `/healthz`, `/readyz`, and `/livez` stay open. The area's resource integration is
`Assimalign.Cohesion.ConfigurationStore.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library.

## Public types

- `ConfigurationStoreApplication` — concrete host and creation entry point.
- `ConfigurationStoreApplicationBuilder` — composition and background-work registration.
- `ConfigurationStoreApplicationContext` — host environment and runtime state.


## Concrete composition (T10 / O34)

`ConfigurationStoreApplication.CreateBuilder(args)` returns the public concrete `ConfigurationStoreApplicationBuilder`; its `Build()` returns the public `ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>`. The public `ConfigurationStoreApplicationContext` implements `IConfigurationStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `ConfigurationStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<ConfigurationStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.

## Dependency injection composition

`ConfigurationStoreApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).
