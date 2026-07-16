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

The root references `Assimalign.Cohesion.Http` and nothing else. No DI, no
configuration, no logging: composition integration is `Web.Hosting`'s one job, and the
root must stay importable by every feature library without dragging a composition
surface along.

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
