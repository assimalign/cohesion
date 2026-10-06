# Assimalign.Cohesion.Web.Hosting

The Web runtime composes a concrete `WebApplication : Host<WebApplicationContext>`
through `WebApplication.CreateBuilder(args)`. It implements the root application and
builder contracts and integrates DI, configuration, logging, HTTP transports, and
the enabled resource's control plane at builder time.

## Composition and lifecycle

Feature verbs extend the root `IWebApplicationBuilder`, whose members this module
implements as explicit shims over `WebApplicationBuilder.Services` registrations
(`IHttpFeature`, `IWebApplicationServer`). Background work is registered through the
concrete `WebApplicationBuilder.AddService` instance or context-factory overload, which
registers an `IHostService`. `Build()` closes registration and runs each service factory
once; services start in registration order before servers and stop in reverse order after
every server drains. The default server drains lame-duck style: it accepts nothing new, tells
every peer the connection is closing (`Connection: close` or `GOAWAY`), lets the requests in
flight finish within the host's shutdown budget, and cancels only what outlives it. Disposing
the application disposes the service provider and every factory-created service.

## Telemetry

The default server traces and measures every request (#1064). Subscribe by name:

- **Traces:** the `ActivitySource` `Assimalign.Cohesion.Web.Hosting` emits one `Server` span per
  request, parented to the caller's W3C `traceparent`, named `GET /orders/{id}` once routing has
  selected the endpoint, and tagged per the OpenTelemetry HTTP server conventions.
- **Metrics:** the `Meter` `Assimalign.Cohesion.Web.Hosting` emits `http.server.request.duration`
  (seconds) and `http.server.active_requests`.
- **Request id:** `context.Features.Get<IWebRequestIdFeature>()?.RequestId` is the request's trace id,
  with or without a listener.

With no listener the server creates no activity and records nothing. Exporting these signals is not
this module's job; see [Design](DESIGN.md), "Server telemetry", for the attributes, the outcomes and
what is deliberately not emitted.

## Dependencies and hosting family

The module references the Web root and its own hosting family within its area (COHRES002), together with
Cohesion's hosting, configuration, DI, logging, and transport infrastructure. Its
`Hosting.Resources` and `Hosting.Health` integrations are runtime concerns; the
Web root references no hosting library. The reusable `Web.Hosting.Resources` and
`Web.Hosting.Health` packages do not reference this module; it consumes
`Web.Hosting.Resources` for the enabled resource's control-plane terminal.

All public composition is explicit and AOT-compatible. See [Design](DESIGN.md) for
listener ownership, cancellation, failure isolation, and control-plane behavior.
