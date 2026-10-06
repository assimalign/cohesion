# Assimalign.Cohesion.Web — Overview

The Web area root: the pipeline and composition abstractions every Web library builds
against. It is contracts-first and feature-free by design.

## Scope

- **Application/builder contracts** — `IWebApplication`, `IWebApplicationBuilder`,
  `IWebApplicationContext`, the `IHostService` application-lifecycle seam, and the
  server seam `IWebApplicationServer`.
- **Response completion** — `IWebResponseCompletionFeature` registers callbacks that run in
  order after the response is written to the transport. The default server installs it per
  exchange; custom servers may omit it. Registration after completion throws.
- **Request id** — `IWebRequestIdFeature.RequestId` is the request's W3C trace id: the server
  span's trace id when the server traces the request, otherwise the trace id of a valid
  `traceparent`, otherwise a random id generated on first read. The default server installs it
  per exchange (#1064); custom servers may omit it.
- **The middleware-first pipeline** — `IWebApplicationPipeline`,
  `IWebApplicationPipelineBuilder`, `IWebApplicationMiddleware`, the
  `WebApplicationMiddleware` delegate, and the inline `Use(...)` adapter sugar in
  `WebApplicationExtensions`.
- **Endpoint selection** — `IWebEndpointFeature` is the endpoint a selecting middleware
  (`UseRouting`) published for the exchange. The pipeline's terminal
  (`WebApplicationTerminal`) runs it after every middleware registered behind the selector,
  or answers a bodyless 404 (#1054). Its `RouteTemplate` is the low-cardinality name the
  server's telemetry reports as `http.route` (#1064).
- **Branching** — `Map(path, branch)`, `MapWhen`, `UseWhen` and `Run` over
  `IWebApplicationPipelineBuilder` (#1056). A path branch publishes `IWebPathBaseFeature`
  (`context.GetPathBase()`, `context.GetEffectivePath()`) instead of rewriting the request.

Feature libraries (`Assimalign.Cohesion.Web.<Feature>`) reference this root and ship
their own `Add<Feature>`/`Use<Feature>` verbs against these seams; the runtime module
(`Assimalign.Cohesion.Web.Hosting`) implements the contracts. The build-enforced
hosting-isolation rule that keeps those two directions apart is documented in
`resources/Web/README.md`.

## Dependencies

`Assimalign.Cohesion.Http`. The root references no `Assimalign.Cohesion.Hosting*`
library; `AddService` belongs to the concrete `Web.Hosting` builder. The root
has no DI, configuration, or logging reference, and it absorbs no
feature models, so referencing it never drags a feature surface along.

## Usage

Applications rarely reference this package directly: `Sdk.Web` delivers the whole
family through the `App.Web` shared framework, and feature verbs (for example
`UseRouting` from `Web.Routing` or `UseForwardedHeaders` from `Web.ForwardedHeaders`)
compose against the `IWebApplicationBuilder`/`IWebApplicationPipelineBuilder` seams
defined here.
