# Assimalign.Cohesion.ConfigurationStore.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. `ConfigurationStoreApplication.CreateBuilder(args)` returns the public concrete `ConfigurationStoreApplicationBuilder`; its `Build()` returns the public `ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>`. The public `ConfigurationStoreApplicationContext` implements `IConfigurationStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Execution model

The builder captures code-first namespace declarations, materializes explicit services once, and
appends the ConfigurationStore HTTP endpoint. Explicit services therefore start before the listener
and stop after it. The area's resource integration is
`Assimalign.Cohesion.ConfigurationStore.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library. The host accepts `--endpoint`
and `--data` overrides (defaulting to `http://127.0.0.1:8080` and `data` under the content root).

## Persistence and protocol

Each namespace is a plain JSON document beneath `data/namespaces`; its file name is the SHA-256
digest of the namespace name, while the document retains the original name. Writes use a temporary
file and atomic replacement. A declaration is written only when that namespace is absent, so
durable values survive restart. Configuration values are not
secret material and are intentionally stored unencrypted. A mutation is committed to the live
snapshot only after its durable replacement succeeds, so a failed write cannot create restart drift.

`GET /cohesion/v1/namespaces` lists names and `?name=` reads one direct key/value object. Health,
readiness, liveness, and the endpoint listing are also served under `/cohesion/v1`.

## Trust

The host accepts requests under
`/cohesion/v1` only when the transport remote endpoint is `127.0.0.1`, `::1`, or IPv4-mapped
`127.0.0.1`. Other peers, including absent or non-IP endpoints, receive 403. The explicit refusal
makes the development-only access rule clear; forwarded headers never establish locality.
Bare `/healthz`, `/readyz`, and `/livez` remain open for orchestrators and load balancers; their
namespaced aliases follow the `/cohesion/v1` loopback rule. These routes are relative to the configured
endpoint base path.

This is a behavior break from 10.0.0-preview.1 (#1431, owner decision 46, accepted on
decision 15's preview terms): a reachable
standalone listener previously accepted remote reads, commands, and stop requests
without credentials. The listener serves no command or stop route.

## Boundaries

The module references only the ConfigurationStore area root among resource packages. DependencyInjection,
Hosting, and the private Web transport
are infrastructure dependencies; no Gateway or other ConfigurationStore feature package is referenced.
JSON is parsed and written explicitly, with no reflection-based serialization.

## Concrete composition (T10 / O34)

Background-work registration belongs to the concrete `ConfigurationStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<ConfigurationStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.

## Dependency injection composition

`ConfigurationStoreApplicationBuilder.Services` is the application's one composition registry, created with `EnableDynamicCode = false`, `ValidateOnBuild = true`, and `ValidateScopes = true`. The module registers only factories and instances, so the provider stays reflection-free. Every lifecycle service is an `IHostService` registration: `AddService(IHostService)` registers the caller's instance, `AddService(Func<ConfigurationStoreApplicationContext, IHostService>)` registers a factory closed over the context, and a direct `Services.AddSingleton<IHostService>(...)` joins the lifecycle the same way. Order is registration order: `ConfigurationEndpointService` registers in `Build`, so it starts last and stops first.

`Build` closes registration (the container becomes read-only, and a second `Build` throws), creates the provider once, and resolves `IEnumerable<IHostService>` once into the snapshot the host reuses for every start, stop, and rollback pass. The application owns the provider: disposal stops the host, then disposes the provider, which disposes every factory-created service in reverse creation order while instance registrations stay with their callers. A failed `Build` disposes the provider before rethrowing. The concrete context exposes the provider as `ServiceProvider` to hosting-layer factories; the area root and its features never see it.

Each `AddNamespace` declaration is a registration, and `ConfigurationStoreRepository` is a singleton seeded from the resolved declarations. `AddNamespace` is an explicit `IConfigurationStoreApplicationBuilder` shim over the public concrete verb, which returns the builder for chaining and still rejects a repeated namespace name at registration. `ConfigurationEndpointService` is disposable; the application now disposes it through the provider.