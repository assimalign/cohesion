# Assimalign.Cohesion.Database.Application — Design

## Design intent

The out-of-process database resource needs a process, and the orchestration plane
already names it: `DatabaseResource.Artifact` is
`"Assimalign.Cohesion.Database.Application"`. This project is that artifact — the
**composition-root executable**, and nothing else: every behavior it exhibits
(engine durability and its worker loops, endpoint drain) lives in the libraries
it composes. If a piece of logic here starts feeling like a feature, it
belongs in a library.

## Target design (pinned — #906)

The executable is the **interim state**. Per the `<Area>.Application` convention
(owner direction, 2026-07-13; `.claude/rules/resource-areas.md`), this project's
target design is the **manifest-generation project**, not an executable: an SDK
consumer loads this specific project, build tasks code-generate the application
manifest from it, and the SDK-specific `Database.ApplicationModel` then helps the
gateway access that manifest — the same convention in every resource area. The
realignment is deliberately pinned on the ApplicationModel program (a separate
beast altogether): [#906](https://github.com/assimalign/cohesion/issues/906).
Everything below documents the interim executable, which stays load-bearing (it
is what the gateway E2E launches) until that issue lands.

## Why-this-not-that decisions

- **A sanctioned COHRES001 exemption, not a rule change.** The hosting-isolation
  rule exists so *libraries* never drag the composition surface into consumers.
  An executable is not a library — it is the analog of a user application, the one
  place composing `Database.Hosting` is the whole point. The csproj declares
  `CohesionHostingIsolationExemptions` for exactly
  `Assimalign.Cohesion.Database.Hosting`, per the opt-out protocol in
  `.claude/rules/resource-areas.md` (precedent: the standing `<Area>.Testing`
  convention for projects that must drive the concrete runtime). The alternative —
  exempting executables from the rule globally in `Build.Rules.targets` — was
  rejected: per-project declarations keep every exception visible and reviewed.
- **A thin `Program` over a testable bootstrap.** `Program.Main` only binds the
  environment, translates `FormatException` into a non-zero exit, and wires the
  shutdown signals. Everything composable lives in the internal
  `DatabaseApplicationBootstrap` (+ `DatabaseApplicationComposition`), so tests
  drive the full env-config → running-host path in-process — no process spawning,
  no flaky stdout scraping.
- **Composed through the area builder pattern (2026-07-13)** — this executable is
  the proof-of-pattern consumer. The bootstrap starts from
  `DatabaseApplication.CreateBuilder()`, registers the SQL engine through the
  model's own verb (`builder.AddSqlDatabase(...)` — shipped by `Database.Sql`
  against the root's `IDatabaseApplicationBuilder` seam; the engine is a data
  machine, operational the moment the verb returns), fronts it with the SQL
  model's server (`builder.AddSqlServer(engine, ...)` over the TCP listener —
  eager, not deferred: a per-model server needs only its one engine, already in
  hand), and parks the default-database provisioner on
  `builder.Options.Services`. Hand-assembling `DatabaseApplicationOptions` was
  the interim shape; the builder is the same options object with the model
  registration inverted to the package that owns the model.
- **Bind all interfaces, gateway-injected port (container-style).** The gateway
  owns the network boundary and injects `COHESION_DATABASE_ENDPOINT_PORT`; inside
  its own (container/pod) boundary the host binds `0.0.0.0`. Loopback-only binding
  was rejected: it would break containerized port mapping, and a developer running
  the bare executable can firewall or simply not expose the port. An unset port
  falls back to an OS-assigned one (dev convenience; the gateway observes the
  bound endpoint post-launch).
- **Durability convention mapping lives here**, in the composition root — not in
  `DatabaseHostConfiguration` (which stays a dumb binder) and not in the engine
  (whose option is already typed). `full`/`synchronous` → per-commit fsync
  (default, also when unset); `grouped`/`relaxed` → the group-commit window
  (batched fsync — never weaker durability, commits still acknowledge only when
  durable). Unrecognized values fail startup loudly rather than silently running
  with a default the operator did not choose.
- **Graceful shutdown via `PosixSignalRegistration`** for SIGINT and SIGTERM (the
  orchestrator's stop): the handler cancels the run token — the host's shutdown
  signal — so the endpoint drains first, and the composition's disposal then
  disposes the engine, quiescing its workers and flushing durably before exit. `ProcessExit` alone was rejected: it races the runtime's
  teardown; the signal registration pre-empts default termination cleanly on all
  platforms.
- **The host provisions a default database (`app`).** The wire protocol has no
  CREATE DATABASE verb (database provisioning is a deployment concern in the MVP),
  so a fresh server process with zero databases would be unusable — no client
  could bind anything. An internal `DefaultDatabaseProvisioner` host service runs
  before the endpoint accepts (services start ahead of the servers): it opens
  `app` when its files exist (restart) and creates it on first launch. The name is a convention
  for now; a `COHESION_DATABASE_NAME` binding (mirrored by a
  `DatabaseResourceOptions` knob so both planes agree) is the natural follow-up
  when multi-database deployments need it.
- **Deliberately not in the `App.Database` framework manifest.** Frameworks
  deliver libraries to applications; this *is* an application. It ships as a
  deployment artifact (published executable / container image), documented in the
  manifest comment.
- **SQL engine only, for now.** The executable composes the engines that exist;
  other model engines join the composition (likely behind configuration) as they
  ship. Multi-engine composition policy is a future decision — hardcoding today's
  one engine is honest.

## Error model

Configuration errors (`FormatException` from the port binder or the durability
mapping) print a `cohesion-db:`-prefixed line to stderr and exit `1` before
anything binds. Runtime faults propagate from the host (`Host<TContext>` rolls back
a failed start; an engine background-worker fault is observable as
`EngineState.Faulted` — the engine keeps serving, degraded).

## AOT posture

Static composition end to end: the bootstrap news up the engine, listener, server,
and application — no reflection, no plugin discovery, no configuration binding
beyond `Environment.GetEnvironmentVariable`. The project builds with the area-wide
`IsAotCompatible` and publishes NativeAOT.

## Non-goals

- No CLI argument surface (environment variables are the resource convention; a
  CLI can layer on later without touching the bootstrap).
- No TLS/authentication configuration yet — the server's authenticator seam
  defaults to the MVP allow-all posture; production authenticators arrive with the
  security build-out (#177 family) and will surface here as configuration.
- No logging/diagnostics surface yet — arrives with the area's health/diagnostics
  work (#168).
