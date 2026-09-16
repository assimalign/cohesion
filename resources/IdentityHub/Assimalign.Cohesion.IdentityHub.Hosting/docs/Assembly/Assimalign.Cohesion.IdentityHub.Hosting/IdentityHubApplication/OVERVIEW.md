# IdentityHubApplication

Namespace: `Assimalign.Cohesion.IdentityHub.Hosting`

Assembly: `Assimalign.Cohesion.IdentityHub.Hosting`

`IdentityHubApplication.CreateBuilder(args)` creates the code-first IdentityHub builder for the executable resource assembly. Build validates registrations, captures ambient resource inputs, resolves the `https` endpoint and `data` mount, materializes additional services in registration order, and appends the built-in issuer service.

The issuer is registered with ResourceRuntime when a generated default control plane exists. `--endpoint` and `--data` provide standalone overrides when no corresponding ambient input is present. A materialized `tls` mount supplies production PEM certificate material; the built-in self-signed fallback and device-approval page are restricted to loopback Local.

## Concrete composition (T10 / O34)

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `IdentityHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<IdentityHubApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
