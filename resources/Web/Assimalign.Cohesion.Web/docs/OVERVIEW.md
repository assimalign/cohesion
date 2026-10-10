# Assimalign.Cohesion.Web — Overview

The Web area root: the pipeline and composition abstractions every Web library builds
against. It holds base contracts and composition seams only, and declares no `IHttpFeature`
contract (owner decision 33, #1379).

## Scope

- **Application/builder contracts** — `IWebApplication`, `IWebApplicationBuilder`,
  `IWebApplicationContext`, and the server seam `IWebApplicationServer`.
- **The middleware-first pipeline** — `IWebApplicationPipeline`,
  `IWebApplicationPipelineBuilder`, `IWebApplicationMiddleware`, and the
  `WebApplicationMiddleware` delegate.
- **Composition verbs** — `WebApplicationExtensions` over `IWebApplicationPipelineBuilder`: the
  inline `Use(...)` adapter, the `UseWhen(predicate, segment)` segment that rejoins the pipeline,
  and `Run(terminal)` terminal middleware (#1056).

What used to live here and where it went (#1379):

| Moved | New home | Namespace |
|---|---|---|
| `IWebEndpointFeature`, `WebApplicationTerminal` | `Assimalign.Cohesion.Web.Routing` | `Assimalign.Cohesion.Web.Routing` |
| `IWebPathBaseFeature`, `Map(path, branch)`, `MapWhen`, `GetPathBase()`, `GetEffectivePath()` | `Assimalign.Cohesion.Web.Routing` | `Assimalign.Cohesion.Web.Routing` |
| `IWebRequestIdFeature`, `IWebResponseCompletionFeature`, `IWebServerDrainFeature` | `Assimalign.Cohesion.Web.Server` | `Assimalign.Cohesion.Web` (unchanged) |
| `UseWhen`, `Run` (class `WebApplicationBranchingExtensions`) | this package, class `WebApplicationExtensions` | `Assimalign.Cohesion.Web` (unchanged) |

Extension-form calls (`app.UseWhen(...)`, `app.Run(...)`) compile unchanged. A static-form call
(`WebApplicationBranchingExtensions.UseWhen(app, ...)`) must name `WebApplicationExtensions`, because
`WebApplicationBranchingExtensions` is now `Web.Routing`'s branching type, and every binary that
used a moved member must be rebuilt.

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
defined here. Design detail: [DESIGN.md](DESIGN.md).
