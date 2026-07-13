# Cohesion Data Platform (`resources/Database/`)

The multi-model OLTP database engine family for Cohesion: five independent database engines — **SQL**, **Documents**, **Graph**, **Blob**, and **KeyValuePair** — sharing one durable kernel (storage, write-ahead logging, transactions, indexing). See [DESIGN.md](DESIGN.md) for the architecture, requirements, and decision log; sequencing lives in [docs/DATABASE_PROGRAM_PLAN.md](../../docs/DATABASE_PROGRAM_PLAN.md).

## Layering

In the repo's L1/L2/L3 model (see `docs/DELIVERY_ROADMAP.md`), this area is **L3.2 — Data Platform**: a service platform built on the L1 foundation libraries (`Core`, `Connections`, `Hosting`, `Security`) and the L2 application runtime (`ApplicationModel`). It ships to consumers through the `Assimalign.Cohesion.Sdk.Database` MSBuild SDK and the `Assimalign.Cohesion.App.Database` shared framework.

## Project catalog

### Kernel (shared by every model)

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Database` | Contract root: `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `DatabaseException`, and the application-composition seam (`IDatabaseApplicationBuilder`/`IDatabaseApplication` — model packages register engines here without knowing the hosting layer) — **rolls up the child roots** (references `Types`/`Language`/`Storage`/`Transactions`/`Execution`/`Protocol`/`Security`/`Governance`; child roots never reference the root) |
| `Assimalign.Cohesion.Database.Storage` | Child root — pages, buffer pool, free-space map, journal (WAL), recovery, backup |
| `Assimalign.Cohesion.Database.Transactions` | Child root — MVCC snapshots, isolation levels, lock manager, transaction log seam, `TransactionId`/`TransactionState` |
| `Assimalign.Cohesion.Database.Indexing` | Order-preserving key encoding, B+Tree/hash index contracts, cursors (consumes the root; not rolled up) |
| `Assimalign.Cohesion.Database.Types` | Child root — shared scalar type system: identity, comparison/collation, binary encoding |
| `Assimalign.Cohesion.Database.Execution` | Child root — query request/result families, execution pipeline contracts |
| `Assimalign.Cohesion.Database.Language` | Child root — shared lexer/parser/diagnostics infrastructure for the model languages |
| `Assimalign.Cohesion.Database.Memory` | In-memory storage strategy (tests, embedded scenarios) |

### Model engines

Each model follows the same matrix: root (engine + public interface), plus `.Language`*, `.Storage`, `.Catalog`, `.Client`, `.Security`, `.Replication` satellites.

| Model | Root project | Notes |
|---|---|---|
| SQL | `Assimalign.Cohesion.Database.Sql` | Parser is substantial; declared-dialect contract pending |
| Documents | `Assimalign.Cohesion.Database.Documents` | OQL-based language contract |
| Graph | `Assimalign.Cohesion.Database.Graph` | Query standard selection (#193) gates language work |
| Blob | `Assimalign.Cohesion.Database.Blob` | API-driven; no `.Language` project |
| KeyValuePair | `Assimalign.Cohesion.Database.KeyValuePair` | Protocol-verb commands; no `.Language` project |
| Cache | `Assimalign.Cohesion.Database.Cache` | Post-MVP; deferred behind KeyValuePair |

### Service surface, hosting, orchestration

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Database.Protocol` | Child root — wire protocol frames and message contracts (shared client/server), `ProtocolVersion` |
| `Assimalign.Cohesion.Database.Client` | Shared client core: connection strings, pooling, protocol client |
| `Assimalign.Cohesion.Database.Security` | Child root — authN/authZ contracts (principals, roles, permissions) |
| `Assimalign.Cohesion.Database.Replication` | Shared replication contracts (WAL log-shipping seam) |
| `Assimalign.Cohesion.Database.Governance` | Child root — quotas, tenancy boundaries, audit events |
| `Assimalign.Cohesion.Database.Hosting` | Host composition (`Host<TContext>`), the area's only DI seam; implements the root's application builder (`DatabaseApplication.CreateBuilder()`); owns the server runtime — network front-end, sessions, auth handshake, frame pump (`Database.Server` folded in 2026-07-12) |
| `Assimalign.Cohesion.Database.ApplicationModel` | Manifest-only orchestration resource + `AddDatabase(...)` |
| `Assimalign.Cohesion.Database.Application` | The standalone host **executable** — the artifact `DatabaseResource` declares (composition root: env conventions → engine + endpoint + host; sanctioned COHRES001 exemption) |
| `Assimalign.Cohesion.Database.Embedded` | In-process consumption facade — how other platform resources embed their data layer |

## Dependencies on other areas

- `libraries/Core` — foundational primitives (everywhere)
- `libraries/Hosting` — host lifecycle + per-service execution menu (`Database.Hosting`)
- `libraries/Connections` — transport drivers for the server runtime (`Database.Hosting`)
- `libraries/ApplicationModel` — orchestration contracts (`Database.ApplicationModel` only)
- `resources/Web` — private implementation detail of the root project (HTTP admin surface); hidden from consumers via `CohesionPrivateProjectReference`

## Building

The area solution is `Assimalign.Cohesion.Database.slnx`. Individual projects build with `dotnet build <project>` (build `build/Tasks` first in a fresh clone/worktree so the custom MSBuild tasks exist).
