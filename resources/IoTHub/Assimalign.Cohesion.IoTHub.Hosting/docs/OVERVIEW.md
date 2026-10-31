# Assimalign.Cohesion.IoTHub.Hosting

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`. Explicit services preserve registration/start order and reverse stop order. The retired resource integration (control plane, health, telemetry) was removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the area's resource integration returns as `Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel`.

Domain service behavior remains deferred.

See [DESIGN.md](DESIGN.md).

## Concrete composition (T10 / O34)

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`; its `Build()` returns the public `IoTHubApplication : Host<IoTHubApplicationContext>`. The public `IoTHubApplicationContext` implements `IIoTHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `IoTHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<IoTHubApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

## Dependency injection composition

`IoTHubApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).
