# Assimalign.Cohesion.Web.ApplicationModel

**Status: intentionally empty — reserved for a future rebuild. Do not add runtime code here.**

## Why this project has no source

This project previously held an abandoned, pre-redesign iteration of what became
`Assimalign.Cohesion.Web.Hosting`: a second `WebApplication` /
`WebApplicationPipelineBuilder` plus an `HttpServer` stack. That tree was untouched
since 2026-05-12, belonged to no parent solution, was referenced by nothing but its
own test project, and could no longer compile — it imported the
`Assimalign.Cohesion.Transports` namespace that was deleted in the 2026-06 Connections
redesign, placed `HttpServer` in the wrong namespace, and left `Dispose()` /
`StopAsync()` throwing `NotImplementedException`.

The dead `src/` content (`WebApplication*`, `Internal/`, and `Server/` including
`HttpServer`, `HttpServerBuilder`, `HttpServerOptions`, and `HttpTransportOptions`) was
removed in the delete slice tracked by
[#761](https://github.com/assimalign/cohesion/issues/761)
(`[L03.01.01.03]`). The `.csproj`, the project-local `.slnx`, and the test project
identity were **deliberately preserved** so the project path can be reused by the
rebuild described below.

## What this project is reserved for

The [ApplicationModel design](../../../libraries/ApplicationModel/DESIGN.md) prescribes
rebuilding this project as a **Layer 3d, manifest-only** resource:

- **§9.4 — Layer 3d `{Resource}.ApplicationModel` (manifest-only):** references
  `Assimalign.Cohesion.ApplicationModel` **only**, provides the `AddWebApp(name)`
  extension plus the `IApplicationResource` / capability implementation, and does
  **not** reference `Web.Application`.
- **§11 Migration Plan, Phase 4 (Web):** "delete the competing `Web.ApplicationModel/src`,
  rebuild as Layer 3d; rename `Web.Hosting` → `Web.Application`; add `WebResource` +
  `AddWebApp`."
- **§9.0** records the same delete-then-rebuild verdict for this tree.

That rebuild is **out of scope** for the delete slice and is deferred to the
ApplicationModel pull-together effort that follows the Web resource assembly. Until then
this project is a reserved placeholder — it should reference `ApplicationModel` only when
rebuilt, never the nine runtime libraries the old iteration pulled in.
