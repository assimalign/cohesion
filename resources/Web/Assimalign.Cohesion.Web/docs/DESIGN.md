# Assimalign.Cohesion.Web — Design

## Design intent

`Assimalign.Cohesion.Web` is the Web area root: the composition seams every Web library
builds against — the application/builder contracts (`IWebApplication`,
`IWebApplicationBuilder`, `IWebApplicationContext`), the middleware-first pipeline
(`IWebApplicationPipeline`, `IWebApplicationPipelineBuilder`,
`IWebApplicationMiddleware`, the `WebApplicationMiddleware` delegate), and the server
seam (`IWebApplicationServer`). Feature packages compose against these; the runtime
module (`Web.Hosting`) implements them; the build-enforced hosting-isolation rule
(`resources/Web/README.md`, `.claude/rules/resource-areas.md`) keeps the two directions
from ever meeting in a library's dependency graph.

The root is deliberately **contracts-only**. Features — their models, options, builder
verbs, and middleware — live in per-concern `Assimalign.Cohesion.Web.<Feature>`
packages, never here (precedent: authentication's model and builder surface live in
`Web.Authentication`; forwarded-headers resolution lives in `Web.ForwardedHeaders`).
The separation is what it protects: the root stays small and stable so every feature
library can reference it without inheriting anyone else's surface, and a feature's
dependency cost is always opt-in. The breakdown signal — this file's reason to exist —
is the root absorbing anything feature- or model-specific; that is an architecture
conversation, not a convenience call.

The root references `Assimalign.Cohesion.Http` and no `Assimalign.Cohesion.Hosting*`
library (O34). `IWebApplication` exposes `Context`, `StartAsync`, and `StopAsync`;
`IWebApplicationBuilder` supplies Web registration verbs and `Build()`. Background-work
registration belongs to the concrete `WebApplicationBuilder` in `Web.Hosting`.
DI, configuration, and logging integration remain builder-time hosting concerns.

## The pipeline model (middleware-first)

The Web area composes request handling as an onion of middleware over `IHttpContext` —
fluent `.Use(...)` registration, `Task InvokeAsync(IHttpContext, WebApplicationMiddleware next)`
execution, registration order = execution order. There is deliberately no return-value
result model (the `IResult` abstraction was withdrawn pre-merge, 2026-07-10 direction):
middleware either writes the response and stops calling `next`, or cooperates by
attaching typed features to `IHttpContext.Features` for downstream stages. That
feature-collection seam — not request-time service location — is the area's
extensibility mechanism, which is why the pipeline contracts here stay this small.

`WebApplicationExtensions` carries the one piece of sugar the root owns: the inline
`Use(Func<IHttpContext, WebApplicationMiddleware, Task>)` adapter that bridges
application lambdas onto the core `Use(Func<WebApplicationMiddleware, WebApplicationMiddleware>)`
registration form.

## Application lifecycle services

The concrete `WebApplicationBuilder.AddService` in `Web.Hosting` accepts an
`IHostService` instance or a factory over the final `IWebApplicationContext`. The
factory runs once at build time. The root builder has no service-registration member
or hosting-library reference; no area-owned service abstraction is introduced (O34).

Application services and Web servers form two ordered phases rather than one interleaved
list: services start first in service-registration order, then servers start in
server-registration order. Host shutdown reverses the full sequence, so every server
drains before application services stop. This ordering holds regardless of whether an
`AddService` call appeared before or after an `AddServer` call in the fluent composition.

## Server lifecycle contract

`IWebResponseCompletionFeature` is the response-transmission seam beside
`IWebApplicationServer`. The default server installs it on every exchange and invokes callbacks
in registration order after writing the response to the transport. Registration after completion
throws `InvalidOperationException`. Custom servers may omit it; middleware must handle a missing
feature. This lets a terminal defer lifecycle signals until its acknowledgement has been sent.

`IWebApplicationServer.StartAsync` is the endpoint-acquisition boundary: it does not
complete until every listener is bound and ready to accept. Binding failures propagate
through startup instead of surfacing later from an accept loop. `StopAsync` is the
symmetric release boundary and does not complete until the endpoints are released.
Hosted restart constructs a fresh server/listener instance after the prior instance
stops; a disposed listener is not rebound.

`IWebApplicationBuilder.AddServer` accepts that contracts-only server directly or through
a factory over the final `IWebApplicationContext`. The Hosting implementation supplies its
own lifecycle adapter, so a server is not required to reference or implement Hosting's
`IHostService`. Multiple servers start in registration order and stop in reverse order;
`IWebApplicationContext.Servers` exposes the original server objects rather than their
runtime adapters.

`AddPipeline` is the complete user-pipeline replacement seam. A Hosting runtime may still
place fixed runtime terminals ahead of the supplied pipeline; replacing user dispatch does
not replace host-owned lifecycle or management surfaces.

## Ordering is registration order

Middleware ordering is positional. Some features carry hard ordering contracts — for
example `Web.ForwardedHeaders` must be registered before anything that consumes client
identity — and each feature package documents its own. Formal, enforceable ordering
rules are the open #26/#145 work; the root intentionally ships no enforcement mechanism
ahead of them.

## AOT posture

Contracts and delegate plumbing only — no reflection, no runtime codegen
(`IsAotCompatible=true`).

## Non-goals

- **Feature models or middleware.** Per-concern packages own them; the root absorbing a
  feature is the architecture smell this design guards against.
- **DI/configuration/logging integration.** `Web.Hosting` composes those, builder-time
  only.
- **A return-value result model.** Withdrawn by design; the pipeline is middleware-first.
