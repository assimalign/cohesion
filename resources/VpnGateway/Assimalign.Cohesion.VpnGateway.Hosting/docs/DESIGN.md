# Assimalign.Cohesion.VpnGateway.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `VpnGatewayApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

## Composition execution model

Each build creates a new context, invokes every registered service factory exactly once against that context, and exposes the materialized services as an ordered, read-only `HostedServices` snapshot. The shared host starts services in registration order and stops them in reverse; an unregistered resource with an unconfigured builder still produces an empty collection and a production `HostEnvironment`.

`TunnelDataPlaneService` remains as a dormant future service stub and is not registered by default.

## Boundaries

The module references the area root and Hosting, Hosting.Health, and Hosting.Resources publicly. Its Web, Web.Hosting, Web.ControlPlane, HTTP, and transport implementation dependencies are private, with their resolved closure supplied by the area runtime framework. Hosting never references its own ApplicationModel package. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

## Enabled resource lifecycle

The builder discovers the entry assembly's registered default control plane through ResourceRuntime.TryCreateControlPlane. The host environment carries the ambient environment name and content root. Build adds the host health contributor and a private http listener when its ambient endpoint exists, then calls ResourceRuntime.HostBuilt. RunAsync delegates to the base host runner seam introduced by 3a62edab (design R6). Without registration, the ordinary explicit-service host remains unchanged and opens no listener.

Web.ControlPlane is installed first on the private listener. It serves the exact v1 resource routes and post-23b command envelopes. Readiness observes the owning area's HostState.Started; managed namespaced routes verify ES256 bootstrap tokens against ApplicationTrustKey. Standalone resources work without a gateway identity. No command kinds or handlers are declared; unsupported commands return 501 with a Rejected body. Domain service stubs remain dormant.
