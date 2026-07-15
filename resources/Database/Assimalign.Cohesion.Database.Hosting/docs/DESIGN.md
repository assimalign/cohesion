# Assimalign.Cohesion.Database.Hosting — Design

## Design intent

`DatabaseApplication` is the standalone host for the database resource. Per the
Cohesion hosting model, each resource type runs as its own `Host<TContext>`
subclass owning its own lifecycle in its own process; this project is that hosting
shell — and, since the 2026-07-13 redesign, it is **composition-only in the
strictest sense**: it wraps composed `IDatabaseServer` instances generically as
endpoint host services, runs any additional services the composition root adds,
and implements the root's application-builder seam. It owns no server machinery
(servers are per-model, implemented inside the model packages — the SQL model's
`SqlDatabaseServer` lives in `Database.Sql`), no engine lifecycle
(engines are data machines — operational from creation, disposed by their
composition root), and no worker scheduling (engines own their loops
unconditionally). Its references shrank accordingly: the area root plus the
non-area `Hosting` foundation — nothing else, not even `Connections`.

## Execution model

Threading is a per-service decision made by static dispatch from the execution
menu defined by `Assimalign.Cohesion.Hosting` (see
`libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `DatabaseServerHostService` (one per registered server) | `BackgroundService` (pool-scheduled) | an async accept loop belongs on the pool |
| Composition-root services (`DatabaseApplicationOptions.Services`) | caller's choice | e.g. the Application executable's default-database provisioner |

Registration order is the additional services first, then one endpoint service
per registered server. A host starts services in registration order and stops
them in reverse, so **the servers start last and drain first** — the unchanged
ordering rule — and provisioning-style services complete before any endpoint
accepts.

Everything that used to sit between those two rows is gone by design:

- **No engine host services.** Engines have no `StartAsync`/`StopAsync` to
  forward (the data-machine decision — root DESIGN.md). The composition root
  creates engines before building the application and disposes them after
  stopping it; durability rides engine disposal, not host stop.
- **No worker slot services.** The engine spawns its own worker loops at
  creation — the latency-critical WAL flusher and page write-back on dedicated
  threads the engine itself owns (the Lane-H dedicated-thread guardrail is
  satisfied inside the engine), checkpoint/maintenance on engine-owned timers —
  and quiesces them on dispose. The host cannot schedule, claim, enable, or
  disable them. See "Worker ownership" below for the reversal record.

## Worker ownership — engine-owned, always (the claim handshake is gone)

The #902 delivery (2026-07-12) let a host *claim* engine workers before engine
start and drive them on its own execution menu (`TryClaim`/`Release`, per-kind
slot options, four named slot services). The 2026-07-13 redesign **deleted that
model**: `IDatabaseEngineWorker` is now observational (name, kind, cadence), the
pump lives on the guided base for the engine's internal use, and the engine is
the one and only scheduler of its loops, from creation to disposal.

Why the reversal: R10 (engine self-sufficiency) already forced the worker
*bodies* and their default scheduling into the engine — the claim handshake only
added a second possible owner for the *pump*, and with it a two-owner protocol
whose failure modes (claim races between host composition and engine start,
disabled slots silently handing loops back, half-claimed inventories across
restarts) each needed rules, tests, and documentation. No composition ever needed
a different scheduler than the engine's own — the host's dedicated-thread slots
were re-implementing exactly the threads the engine spawns for itself. One owner
means a worker can never run twice, with no handshake to verify. The execution
menu still matters — for the services this module *does* compose (endpoint on the
pool) — but engine durability threading is the engine's internal affair.

What survives for hosts: observability. `IDatabaseEngine.Workers` (name, kind,
interval) and the engine's observational `State` (`Running`/`Faulted`/`Disposed`)
are the surface a health endpoint (#168) reads; a worker fault flips the engine
to `Faulted` without stopping service (durability self-help holds — see the Sql
DESIGN.md).

## Why-this-not-that

### The server machinery moved out — servers are per-model (2026-07-13, settled 2026-07-14)

The wire-protocol server was folded INTO this module on 2026-07-12 (mirroring
`Web.Server`→`Web.Hosting`). The 2026-07-13 redesign **half-unwound that fold**:
the approved architecture makes servers per-model (`SqlDatabaseServer` fronting
one `SqlDatabaseEngine`), and COHRES001 makes this module unreferenceable by
area libraries — so server machinery living here is unreachable by exactly the
packages that need it. The machinery's placement then settled through evidence
discipline (area DESIGN decision log): a shared `Database.Server` base above
the root (2026-07-13) → judged premature abstraction from n=1 and folded into
`Database.Sql` (2026-07-14) → the second model server fired the recorded
extraction trigger and the proven core was extracted back out → **the owner
reviewed the extraction evidence and chose per-model duplication (2026-07-14,
the settled placement)**: the shared library was removed and each model package
carries its own full copy of the machinery (the preserved evidence table lives
in the area DESIGN §3.10). The root's `IDatabaseServer` contract is the only
area-wide server requirement. What the 2026-07-12 fold got right is retained
here: composing servers into a host process is this module's job — it wraps any
`IDatabaseServer` in `DatabaseServerHostService`, registered last. This module
references **no model package**: it composes through the root's
`IDatabaseServer` seam alone, which is what keeps it transport-free (no
`Connections` reference).

### One host service shape per server, plural servers

`IDatabaseApplicationContext.Servers` is plural — one server per model the
application serves. Each registered server is wrapped in its own
`DatabaseServerHostService`; they start in registration order and drain in
reverse. The earlier singular `DatabaseApplicationOptions.Server` shape (one
endpoint per application, enforced by an `AddServer`-throws-on-second rule)
was superseded by the per-model server decision — plurality is structural now.

### The host drives servers, not engines

The pre-redesign application registered a per-engine lifecycle service first so
engines started before everything and stopped last. With engines as data
machines there is nothing to drive: an engine registered on the application
(`DatabaseApplicationOptions.Engines`) is an **observational** entry on the
context — the composition root that created it owns it. Durability-on-shutdown
moved from "host stops engines last" to "composition root disposes engines after
the host stops," which the Application executable's composition object does in
dependency order (application → server → listener → engine).

## Configuration conventions

`DatabaseHostConfiguration.FromEnvironment()` binds the environment-variable
conventions a gateway injects when it launches the host —
`COHESION_DATABASE_DATA_PATH`, `COHESION_DATABASE_ENDPOINT_PORT`,
`COHESION_DATABASE_DURABILITY`. Binding lives here because the hosting module is
the area's one Configuration seam; the bound values shape how the composition
root builds the engine (data path, durability) and the listener (port). The
`Database.ApplicationModel` resource sets the same variable names on its realized
process, so the manifest side and the host side agree by convention (the two
projects share no assembly).

## The builder-first composition surface

`DatabaseApplication.CreateBuilder()` is the composition entry point, following
the `WebApplication.CreateBuilder()` idiom. The split of responsibilities:

- **The root's `IDatabaseApplicationBuilder`** carries what model packages need:
  engine registration (`AddEngine` — server-less, embedded registrations) and
  server registration (`AddServer` — an instance, or a factory deferred to
  `Build` that receives the **application context**, mirroring the Web area's
  context-receiving factory). Model verbs like `Database.Sql`'s
  `AddSqlDatabase(...)` / `AddSqlServer(...)` compose against this seam only, so
  a model registers itself **without knowing the hosting layer** — registration
  is dependency-free (values and options objects; no container).
- **This module's `DatabaseApplicationBuilder`** implements the seam over a
  `DatabaseApplicationOptions` instance and exposes it (`builder.Options`) for
  the hosting-only surface the root interface deliberately omits: additional
  host services. Deferred server factories resolve at `Build()` in registration
  order against the live context (instance registrations are wrapped as trivial
  factories, so ordering is registration-faithful across both overloads); the
  context wraps the live option lists, so a factory observes every registration
  made before it — engines *and* earlier servers. `Build()` returns the concrete
  `DatabaseApplication` (the guided richer signature; the interface member
  forwards), which implements the root's `IDatabaseApplication` — `Context` +
  start/stop, the Web shape.
- Direct construction (`new DatabaseApplication(options)`) remains supported for
  fully manual hosts; the builder is sugar over the same options object, never a
  second composition model.

The `Database.Application` executable is the proof-of-pattern consumer: its
bootstrap registers the SQL engine through `AddSqlDatabase`, fronts it with
`AddSqlServer` over the TCP listener, and parks the default-database provisioner
on `builder.Options.Services`.

## Status and non-goals

- No DI-container surface on the builder — registration stays values/options
  only, per the area composition rules (`*.Hosting` remains the DI seam for
  everything else).
- No governance/quotas (#167) or health/readiness (#168) surfaces yet — separate
  features (the health surface will read the engines' and servers' observational
  contexts; see the area DESIGN.md next-iteration scoping).
- No server machinery — servers are per-model and live inside the model
  packages (`SqlDatabaseServer` in `Database.Sql`); this module composes them
  through the root's `IDatabaseServer` seam.
- No HTTP admin surface — that is the root `Database` project's private-Web
  concern, deliberately separate from the wire protocol path.
- Direct references: the area root and the non-area `Hosting` foundation.
  Nothing else — no `Connections`, no `CohesionHostingIsolationExemptions`.

## AOT posture

Static composition: the composition root hands the application its servers and
engines; nothing is discovered at runtime. No reflection.
