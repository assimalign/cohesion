# Cohesion — Developer-Experience Design

> **Status (2026-10-31).** This document holds the developer-experience decisions in force: how a
> project picks a Cohesion SDK, which frameworks it gets, the canonical runtime API, and the
> hosting decisions the rules cite by id. The application model is being rebuilt under
> [`fabric/`](../fabric/README.md). The area rules live in `.claude/rules/resource-areas.md`.

## 1. Mental model

**Every deployable thing is an ordinary .NET project that picks a Cohesion SDK; every relationship
is an ordinary project or package reference.**

- A **resource** is an executable on `Assimalign.Cohesion.Sdk.<Area>` (`Sdk.Web`,
  `Sdk.Database`, …): a `Program.cs` with top-level statements that composes the area's
  application through the area builder and runs it, like any .NET executable. Its domain content
  (schemas, policies, pipelines) is application code in that project.

## 2. SDKs and frameworks

| Project kind | SDK | Frameworks it receives | Output |
| --- | --- | --- | --- |
| Resource | `Assimalign.Cohesion.Sdk.<Area>` → `Assimalign.Cohesion.Sdk` → `Microsoft.NET.Sdk` | `App` (the hosting kernel) and `App.<Area>` | Executable |
| Library or tool | `Assimalign.Cohesion.Sdk` (build tooling only) | None unless referenced explicitly | Any |

- **Chained SDKs.** Each area SDK chains to the base SDK, which chains to `Microsoft.NET.Sdk`. The
  consumer's `global.json` pins every Cohesion SDK in the chain; a chained `<Import Sdk>` never
  carries a version. Packaging details are in `.claude/rules/build-system.md`.
- **Framework delivery.** `App` carries the hosting kernel. `App.<Area>` carries the area's
  libraries and its hosting module. Applications get the family with no package references.
  Set `CohesionAutoIncludeAppFramework=false` to opt out.

## 3. The canonical runtime API

Writing a resource is a `Program.cs` that calls `<Area>Application.CreateBuilder(args)`, which
returns the area's public concrete builder; its `Build()` returns the public concrete
`Host<TContext>` application, run with `RunAsync()`. The area root defines the dependency-free
seam (`I<Area>ApplicationBuilder`, `I<Area>Application`), the `<Area>.Hosting` module implements
it, and feature packages ship their own registration verbs. Background work is registered with
`AddService` on the concrete builder, never on the root contract.

## 4. Decisions in force

| Id | Decision | Status |
| --- | --- | --- |
| O15 | The L02 unification epics #17/#18/#19/#130–#132 are closed as superseded: `IHost` is the lifecycle surface, per-area builders are the composition seam, `IApplication` is not `IHost`, and `IHostBuilder` is a `Build()`-only marker; #304 is re-scoped to the nested-lifecycle fixes and #305 is satisfied by the per-area seam. | Landed; the `AddService` half is superseded by O34. |
| O20 | Keep one SDK per area so each area can auto-reference its framework and ship its own build tasks. | Landed |
| O34 | Area roots and feature libraries reference no `Assimalign.Cohesion.Hosting*` library; `AddService` lives on the concrete `<Area>.Hosting` builders; `COHRES004` enforces it. Reaffirmed 2026-10-11 for Database: the root `IDatabaseApplicationBuilder.Build()` stays parameterless, like all 18 areas; the cancellable `Build(CancellationToken)` belongs only to the concrete `DatabaseApplicationBuilder`. | Landed (owner, 2026-09-15) |
| O34a | Database engines are concrete-first: `resources/Database/**` uses sealed concrete types, plus abstract bases with public non-virtual members over protected abstract cores where real variants exist, where `Database.Hosting` must stay model-agnostic, or where a lower assembly drives a seam a higher assembly or the application implements. The `I<Area>Application` seam is unchanged. Rule: `.claude/rules/database-area.md`; plan of record: [DATABASE_CONCRETE_TYPES_PLAN.md](programs/DATABASE_CONCRETE_TYPES_PLAN.md). | Accepted (owner, 2026-10-04; amended 2026-10-06, 2026-10-09, 2026-10-16) |
| O35 | `COHRES001` permits references to the exact `<Area>.Hosting` module from named exemption holders. `<Area>.Testing` holds the standing exemption. Roots and features may not reference `<Area>.Hosting.<Suffix>` integrations. `COHRES002` lets the exact module reference any library in its own area except `<Area>.Testing`, the framework producers, and harnesses. | Landed (owner, 2026-09-16; relaxed 2026-10-09) |
| R6 | Endpoints are `System.Uri` everywhere, with a Core `UriExtensions` block (C# 14 `extension(Uri)`) for what `Uri` lacks; no Cohesion endpoint-address type. | Landed (owner, 2026-09-07) |
| R9 | The runtime contract's .NET home is Core's `AppEnvironment`: its readers, and the constants and name builders in the nested `AppEnvironment.Variables` ([RUNTIME_CONTRACT.md](RUNTIME_CONTRACT.md)). | Landed (2026-09-26) |
