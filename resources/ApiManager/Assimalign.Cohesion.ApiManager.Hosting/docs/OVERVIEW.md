# Assimalign.Cohesion.ApiManager.Hosting

`ApiManagerApplication.CreateBuilder(args)` returns the public concrete `ApiManagerApplicationBuilder`. Explicit services preserve registration/start order and reverse stop order. The retired resource integration (control plane, health, telemetry) was removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the area's resource integration is `Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel`, which references this module; this module references no application-model library.

Domain service behavior and command kinds remain deferred.

See [DESIGN.md](DESIGN.md).

## Concrete composition (T10 / O34)

`ApiManagerApplication.CreateBuilder(args)` returns the public concrete `ApiManagerApplicationBuilder`; its `Build()` returns the public `ApiManagerApplication : Host<ApiManagerApplicationContext>`. The public `ApiManagerApplicationContext` implements `IApiManagerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `ApiManagerApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<ApiManagerApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

## Dependency injection composition

`ApiManagerApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).
