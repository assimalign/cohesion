# Assimalign.Cohesion.EventHub.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. `EventHubApplication.CreateBuilder(args)` returns the public concrete `EventHubApplicationBuilder`; its `Build()` returns the public `EventHubApplication : Host<EventHubApplicationContext>`. The public `EventHubApplicationContext` implements `IEventHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Filler execution model

Without a generated control-plane registration, the built host exposes the ordered `HostedServices` materialized from explicit builder registrations and a production `HostEnvironment`; the collection remains empty when nothing is registered. Instance and factory registrations share one order, factories run exactly once per `Build()` after the context exists, and a null factory result fails the build. Services start in registration order and stop in reverse registration order through the shared host lifecycle without claiming that event-hub behavior exists.

`PartitionFlushService` and `IngressEndpointService` remain as dormant future service stubs. The filler builder does not register them automatically.

## Boundaries

The module references the area root and Hosting, Hosting.Health, and Hosting.Resources publicly. Its Web, Web.Hosting, Web.Hosting.Resources, HTTP, and transport implementation dependencies are private, with their resolved closure supplied by the area runtime framework. Hosting never references its own ApplicationModel package. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

## Enabled resource lifecycle

The builder discovers the entry assembly's registered default control plane through ResourceRuntime.TryCreateControlPlane. The host environment carries the ambient environment name and content root. Build adds the host health contributor and a private http listener when its ambient endpoint exists, then calls ResourceRuntime.HostBuilt. RunAsync delegates to the base host runner seam introduced by 3a62edab (design R6). Without registration, the ordinary explicit-service host remains unchanged and opens no listener.

Web.Hosting.Resources is installed first on the private listener. It serves the exact v1 resource routes and post-23b command envelopes. Readiness observes the owning area's HostState.Started; managed namespaced routes verify ES256 bootstrap tokens against ApplicationTrustKey. Standalone resources work without a gateway identity. No command kinds or handlers are declared; unsupported commands return 501 with a Rejected body. Domain service stubs remain dormant.

## HTTPS endpoint certificate contract (31t)

The enabled resource's `http` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged.

## Optional telemetry (31b)

The registered resource constructor calls ResourceTelemetry.Configure using the invocation snapshot. With no gateway or telemetry endpoint, existing providers and hosted services are unchanged. When enabled, the shared Hosting.Telemetry sibling adds OTLP/HTTP JSON logging and a service registered before producers; reverse StopAsync drains producers before a flush bounded by five seconds and the host shutdown token. Logging remains composed only in Hosting. See libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md for ordering and protocol limits.

## Concrete composition (T10 / O34)

Background-work registration belongs to the concrete `EventHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<EventHubApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
