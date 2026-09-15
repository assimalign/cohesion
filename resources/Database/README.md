# Cohesion Data Platform (`resources/Database/`)

The multi-model OLTP database engine family for Cohesion: five independent database engines — **SQL**, **Documents**, **Graph**, **Blob**, and **KeyValuePair** — sharing one durable kernel (storage, write-ahead logging, transactions, indexing). A hosted database is an ordinary customer-owned `Sdk.Database` executable whose `Program.cs` composes its engines, code-first schema, servers, and provisioning. See [DESIGN.md](DESIGN.md) for the architecture, requirements, and decision log; sequencing lives in [docs/DATABASE_PROGRAM_PLAN.md](../../docs/DATABASE_PROGRAM_PLAN.md).

## Layering

In the repo's L1/L2/L3 model (see `docs/DELIVERY_ROADMAP.md`), this area is **L3.2 — Data Platform**: a service platform built on the L1 foundation libraries (`Core`, `Connections`, `Hosting`, `Security`) and the L2 application runtime (`ApplicationModel`). It ships to consumers through the `Assimalign.Cohesion.Sdk.Database` MSBuild SDK and the `Assimalign.Cohesion.App.Database` shared framework.

## Project catalog

### Kernel (shared by every model)

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Database` | Contract root: `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `DatabaseException` and its exact `DatabaseNotFoundException` absence signal, the application-composition seam (`IDatabaseApplicationBuilder`/`IDatabaseApplication`), and the code-first schema compiler (`IDatabaseSchema` → validated, canonical `CompiledSchema`) plus deterministic migration planner — **rolls up the child roots** (references `Types`/`Language`/`Storage`/`Transactions`/`Execution`/`Indexing`/`Protocol`/`Security`/`Governance`; child roots never reference the root) |
| `Assimalign.Cohesion.Database.Storage` | Child root — pages, buffer pool, free-space map, journal (WAL), recovery, backup |
| `Assimalign.Cohesion.Database.Transactions` | Child root — MVCC snapshots, isolation levels, lock manager, transaction log seam, `TransactionId`/`TransactionState` |
| `Assimalign.Cohesion.Database.Indexing` | Order-preserving key encoding, B+Tree/hash index contracts, cursors (child root; rolled up by the root) |
| `Assimalign.Cohesion.Database.Types` | Child root — shared scalar type system: identity, comparison/collation, binary encoding |
| `Assimalign.Cohesion.Database.Execution` | Child root — query request/result families, execution pipeline contracts |
| `Assimalign.Cohesion.Database.Language` | Child root — shared lexer/parser/diagnostics infrastructure for the model languages |
| `Assimalign.Cohesion.Database.Memory` | In-memory storage strategy (tests, embedded scenarios) |

### Model engines

Each model follows the same matrix: root (engine + public interface), plus `.Language`*, `.Storage`, `.Catalog`, `.Client`, `.Security`, `.Replication` satellites.

| Model | Root project | Notes |
|---|---|---|
| SQL | `Assimalign.Cohesion.Database.Sql` | Ships the SQL engine, compiled-schema migration renderer/provisioner, the model's wire-protocol server (`SqlDatabaseServer`), the `SqlDatabaseServerOptions.Listen(Uri)` endpoint bridge, and the model builder verbs; declared dialect in `Sql.Language` |
| Documents | `Assimalign.Cohesion.Database.Documents` | OQL-based language contract |
| Graph | `Assimalign.Cohesion.Database.Graph` | Query standard selection (#193) gates language work |
| Blob | `Assimalign.Cohesion.Database.Blob` | API-driven; no `.Language` project |
| KeyValuePair | `Assimalign.Cohesion.Database.KeyValuePair` | **Delivered** — ordered key space on the shared kernel (index-primary composition, etag CAS) and the model's wire-protocol server (`KeyValueDatabaseServer`); command grammar in `docs/COMMANDS.md`; no `.Language` project |
| Cache | `Assimalign.Cohesion.Database.Cache` | Post-MVP; deferred behind KeyValuePair |

### Service surface, hosting, orchestration

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Database.Protocol` | Child root — wire protocol frames and message contracts (shared client/server), `ProtocolVersion` |
| `Assimalign.Cohesion.Database.Client` | Shared client core: connection strings, pooling, protocol client, and `DatabaseConnectionSettings.For(Uri)` for generated or ambient endpoints |
| `Assimalign.Cohesion.Database.Security` | Child root — authN/authZ contracts (principals, roles, permissions) |
| `Assimalign.Cohesion.Database.Replication` | Shared replication contracts (WAL log-shipping seam) |
| `Assimalign.Cohesion.Database.Governance` | Child root — quotas, tenancy boundaries, audit events |
| `Assimalign.Cohesion.Database.Hosting` | Host composition (`Host<TContext>`), the area's only DI seam; implements `DatabaseApplication.CreateBuilder(args)`, which honors an enabled executable's ambient `Hosting.Resources` `ResourceContext` and generated default-control-plane registration and stays plain otherwise. Additional services, including `builder.Provision`/`AddDatabase`, start before the per-model servers, so provisioning always precedes accept. The internal admin service privately hosts `Web.Hosting` + `Web.Health` for health, readiness, liveness, endpoint observation, commands, and graceful stop. |
| `Assimalign.Cohesion.Database.ApplicationModel` | Manifest-backed `DatabaseResource : PlannedResource`, `AddDatabase(manifest, options)`, the platform-neutral Database planner (stable identity, sized per-replica volume claims, one headless governing service), and the Database default-control-plane factory registered by generated executable code through `Hosting.Resources` |
| `Assimalign.Cohesion.Database.Testing` | The area's sole hosting-isolation exemption holder; `DatabaseApplicationTestFactory.FromProgram<Program>()` runs the resource's real entry point inside `Hosting.Resources` `ResourceRuntime.CreateScope(...)`, waits on the `admin` control plane, and stops it through the graceful control-plane path |
| `samples/Assimalign.Cohesion.Database.SampleHost` | Non-packable `Sdk.Database` executable with `CohesionApplicationModel=enabled` and build-time schema compilation; composes a SQL table/index schema and the TCP server in `Program.cs`, provisions that compiled schema before accept, and supplies the real-process E2E apphost (`ReferenceOutputAssembly=false`) |
| `Assimalign.Cohesion.Database.Embedded` | In-process consumption facade — how other platform resources embed their data layer |

## Dependencies on other areas

- `libraries/Core` — foundational primitives (everywhere)
- `libraries/Hosting/Assimalign.Cohesion.Hosting` — host lifecycle and the per-service execution
  menu exposed by the concrete `DatabaseApplicationBuilder.AddService` verb in
  `Database.Hosting`. The Database root and feature libraries reference no hosting library (O34).
- `libraries/Hosting/Assimalign.Cohesion.Hosting.Resources` — the opt-in resource runtime,
  context, control-plane, and protected-mount contracts used by enabled executables and
  `Database.ApplicationModel`
- `libraries/Hosting/Assimalign.Cohesion.Hosting.Health` — transport-neutral health contribution
  contracts used by `Database.Hosting` and its default control plane
- `libraries/Connections` — transport drivers for the per-model servers (`Database.Sql`'s `SqlDatabaseServer`, and every future model's server)
- `libraries/ApplicationModel` — orchestration contracts (`Database.ApplicationModel` only)
- `resources/Web` — private implementation details: the root's existing Web dependency plus
  `Web.Hosting`/`Web.Health` in `Database.Hosting` for the `admin` control plane. The coordinated
  `CohesionPrivateProjectReference`/`CohesionFrameworkPrivateAssembly` path keeps that closure in
  the `App.Database` runtime pack and out of its reference surface.

## Building

The area solution is `Assimalign.Cohesion.Database.slnx`. Individual projects build with `dotnet build <project>` (build `build/Tasks` first in a fresh clone/worktree so the custom MSBuild tasks exist).
