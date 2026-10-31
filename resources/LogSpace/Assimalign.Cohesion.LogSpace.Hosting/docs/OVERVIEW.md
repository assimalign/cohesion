# LogSpace Hosting overview

The retired resource integration was removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the area's resource integration returns as `Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel`.

POST /v1/logs requires application/json. Query accepts resource, since (ISO-8601), limit (default 100/max 1000), and cursor, returning application/x-ndjson and X-Cohesion-Next-Cursor when more scanning is possible. Query pages are bounded to four million characters (plus at most one bounded record).

Query under `/cohesion/v1` accepts only transport peers
at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1`; other callers receive `403 Forbidden`. Forwarded
headers do not change this decision. This tightens standalone
behavior from 10.0.0-preview.1.

## Concrete composition (T10 / O34)

`LogSpaceApplication.CreateBuilder(args)` returns the public concrete `LogSpaceApplicationBuilder`; its `Build()` returns the public `LogSpaceApplication : Host<LogSpaceApplicationContext>`. The public `LogSpaceApplicationContext` implements `ILogSpaceApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `LogSpaceApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<LogSpaceApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

## Dependency injection composition

`LogSpaceApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).
