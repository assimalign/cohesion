# Assimalign.Cohesion.NotificationHub.Hosting

`NotificationHubApplication.CreateBuilder(args)` returns the public concrete `NotificationHubApplicationBuilder`. Explicit services preserve registration/start order and reverse stop order. Enabled resources discover their area control plane and serve health, readiness, liveness, endpoint discovery, stop, and command envelopes on the ambient `http` endpoint (http). The plain host opens no listener without registration.

Managed namespaced routes use ES256 bootstrap verification. The private Web implementation stays out of consumer reference packs. Domain service behavior and command kinds remain deferred.

See [DESIGN.md](DESIGN.md).

## Concrete composition (T10 / O34)

`NotificationHubApplication.CreateBuilder(args)` returns the public concrete `NotificationHubApplicationBuilder`; its `Build()` returns the public `NotificationHubApplication : Host<NotificationHubApplicationContext>`. The public `NotificationHubApplicationContext` implements `INotificationHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `NotificationHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<NotificationHubApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
