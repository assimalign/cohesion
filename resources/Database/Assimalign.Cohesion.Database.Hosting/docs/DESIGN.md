# Assimalign.Cohesion.Database.Hosting — Design

## Design intent

`DatabaseApplication` is the standalone host for the database engine resource. Per the
Cohesion hosting model, each resource type runs as its own `Host<TContext>` subclass
owning its own lifecycle in its own process; this project is that hosting shell,
composing the resource's units of work as hosted services and serving as the area's one
DI/Configuration/Logging seam.

## Execution model

Threading is a per-service decision made by static dispatch from the execution menu
defined by `Assimalign.Cohesion.Hosting` (see
`libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md`):

| Service | Menu member | Why |
| --- | --- | --- |
| `WriteAheadFlushService` | `DedicatedThreadService` (dedicated OS thread) | a synchronous blocking flush loop must own its thread for its whole life |
| `PageWriterService` | `DedicatedThreadService` (dedicated OS thread) | a synchronous blocking page write-back loop must own its thread for its whole life |
| endpoint host service (composed) | `BackgroundService` (pool-scheduled) | an async accept loop belongs on the pool |

Registration order is durability workers first, then the composed endpoint services.
A host starts services in registration order and stops them in reverse, so endpoints
start last and stop first — connections drain ahead of the durability workers.

## Why-this-not-that

### The endpoint host service is *composed*, not owned by this module

The wire-protocol server lives in `Database.Server`. The **resource hosting-isolation
rule (COHRES002)** forbids the hosting module from referencing any same-area library
except the area root, so this module cannot name `IDatabaseServer` to compose the
server itself. Two options were possible: (a) promote the server contract into the area
root (the Web area's shape — `IWebApplicationServer` lives in the `Web` root), or
(b) keep `Database.Server` as a distinct service-surface library and let it expose an
`IHostService` endpoint adapter the host composes generically. This build takes (b):
`DatabaseServer.CreateHostService(server)` returns an `IHostService` (the adapter is
`Database.Server → Assimalign.Cohesion.Hosting`, a non-area reference, which is
allowed), and the composition root adds it to `DatabaseApplicationOptions.Services`.
The host stays COHRES002-clean (root + non-area hosting only). Promoting the server
contract into the root (option a) is a larger area refactor and is deferred; it is the
natural follow-up if the area later wants the host to own the endpoint directly.

This is a change from the earlier scaffold, which placed a `QueryEndpointService`
inside this module — that would have required a `Database.Server` reference, which the
build now rejects. The endpoint background service now lives with the server as
`DatabaseServerHostService`.

### Durability worker slots are documented placeholders, not owners of durability

Requirement R10 (the platform data layer) mandates **engine self-sufficiency**: an
engine owns its durability whether embedded or hosted, so `Database.Hosting` is
composition-only. The SQL engine today flushes synchronously at commit (steal/no-force
WAL) and writes pages back inside its own storage layer, so there is **no host-driven
flush or page-writer work to do** — and there must not be, or an embedded consumer
(no host) would silently lose it. `WriteAheadFlushService`/`PageWriterService` are
therefore the execution-menu *slots* for a future engine-owned background
checkpoint/flush worker: they park until shutdown and are documented as such. When the
engine grows a host-mappable background-worker seam, these slots drive it. That engine
seam is out of scope here (adding background workers to the engine is engine-self-
sufficiency work); it is a filed follow-up under feature #862. The slots are on by
default (to keep the standalone host's execution-menu shape) and can be toggled off for
embedded/self-sufficient composition.

### Engine lifecycle stays with the composition root

`IDatabaseEngine` carries no start/stop on its contract (the concrete engines expose
their own `StartAsync`/`StopAsync`). The host therefore serves engines the composition
root started; it exposes them on the context but does not drive their lifecycle. A
root-level engine-lifecycle seam is part of the same deferred hosting-alignment
follow-up.

## Configuration conventions

`DatabaseHostConfiguration.FromEnvironment()` binds the environment-variable
conventions a gateway injects when it launches the host — `COHESION_DATABASE_DATA_PATH`,
`COHESION_DATABASE_ENDPOINT_PORT`, `COHESION_DATABASE_DURABILITY`. Binding lives here
because the hosting module is the area's one Configuration seam; the bound values shape
how the composition root builds the engine (data path, durability) and the listener
(port). The `Database.ApplicationModel` resource sets the same variable names on its
realized process, so the manifest side and the host side agree by convention (the two
projects share no assembly).

## Status and non-goals

- No builder or DI container surface yet; construct `DatabaseApplication` with
  `DatabaseApplicationOptions` directly. A `CreateBuilder` surface can follow the
  `WebApplication` pattern when the resource matures.
- No governance/quotas (#167) or health/readiness (#168) surfaces yet — separate
  features.
- The project references the area root and the non-area hosting foundation only.
