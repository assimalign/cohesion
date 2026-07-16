# Assimalign.Cohesion.Web — Overview

The Web area root: the pipeline and composition abstractions every Web library builds
against. It is contracts-first and feature-free by design.

## Scope

- **Application/builder contracts** — `IWebApplication`, `IWebApplicationBuilder`,
  `IWebApplicationContext`, and the server seam `IWebApplicationServer`.
- **The middleware-first pipeline** — `IWebApplicationPipeline`,
  `IWebApplicationPipelineBuilder`, `IWebApplicationMiddleware`, the
  `WebApplicationMiddleware` delegate, and the inline `Use(...)` adapter sugar in
  `WebApplicationExtensions`.

Feature libraries (`Assimalign.Cohesion.Web.<Feature>`) reference this root and ship
their own `Add<Feature>`/`Use<Feature>` verbs against these seams; the runtime module
(`Assimalign.Cohesion.Web.Hosting`) implements the contracts. The build-enforced
hosting-isolation rule that keeps those two directions apart is documented in
`resources/Web/README.md`.

## Dependencies

`Assimalign.Cohesion.Http` only. The root deliberately has no DI, configuration, or
logging references — composition integration is `Web.Hosting`'s job — and it absorbs no
feature models, so referencing it never drags a feature surface along.

## Usage

Applications rarely reference this package directly: `Sdk.Web` delivers the whole
family through the `App.Web` shared framework, and feature verbs (for example
`UseRouting` from `Web.Routing` or `UseForwardedHeaders` from `Web.ForwardedHeaders`)
compose against the `IWebApplicationBuilder`/`IWebApplicationPipelineBuilder` seams
defined here.
