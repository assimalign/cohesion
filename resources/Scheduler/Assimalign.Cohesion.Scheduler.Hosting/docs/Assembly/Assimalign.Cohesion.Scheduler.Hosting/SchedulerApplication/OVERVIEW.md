# SchedulerApplication

Namespace: Assimalign.Cohesion.Scheduler.Hosting

Assembly: Assimalign.Cohesion.Scheduler.Hosting

SchedulerApplication.CreateBuilder(args) creates the public concrete SchedulerApplicationBuilder and discovers an enabled resource control-plane registration from the entry assembly. A null argument array throws ArgumentNullException.

Build materializes caller services, validates schedule bindings, installs the http control-plane service when an ambient endpoint exists, and adds the provider execution service. RunAsync owns the full shared-host lifecycle.

## Concrete composition (T10 / O34)

`SchedulerApplication.CreateBuilder(args)` returns the public concrete `SchedulerApplicationBuilder`; its `Build()` returns the public `SchedulerApplication : Host<SchedulerApplicationContext>`. The public `SchedulerApplicationContext` implements `ISchedulerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `SchedulerApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SchedulerApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
