# Cohesion Data Platform (`resources/Database/`)

The multi-model OLTP database engine family for Cohesion: five independent database engines — **SQL**, **Documents**, **Graph**, **Blob**, and **KeyValuePair** — sharing one durable kernel (storage, write-ahead logging, transactions, indexing). See [DESIGN.md](DESIGN.md) for the architecture, requirements, and decision log; sequencing lives in [docs/DATABASE_PROGRAM_PLAN.md](../../docs/DATABASE_PROGRAM_PLAN.md).

## Layering

In the repo's L1/L2/L3 model (see `docs/DELIVERY_ROADMAP.md`), this area is **L3.2 — Data Platform**: a service platform built on the L1 foundation libraries (`Core`, `Connections`, `Hosting`, `Security`) and the L2 application runtime (`ApplicationModel`). It ships to consumers through the `Assimalign.Cohesion.Sdk.Database` MSBuild SDK and the `Assimalign.Cohesion.App.Database` shared framework.

## Project catalog

### Kernel (shared by every model)

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Database` | Contract root: `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `DatabaseException` |
| `Assimalign.Cohesion.Database.Storage` | Pages, buffer pool, free-space map, journal (WAL), recovery, backup |
| `Assimalign.Cohesion.Database.Transactions` | MVCC snapshots, isolation levels, lock manager, transaction log seam |
| `Assimalign.Cohesion.Database.Indexing` | Order-preserving key encoding, B+Tree/hash index contracts, cursors |
| `Assimalign.Cohesion.Database.Types` | Shared scalar type system: identity, comparison/collation, binary encoding |
| `Assimalign.Cohesion.Database.Execution` | Query request/result families, execution pipeline contracts |
| `Assimalign.Cohesion.Database.Language` | Shared lexer/parser/diagnostics infrastructure for the model languages |
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
| `Assimalign.Cohesion.Database.Protocol` | Wire protocol frames and message contracts (shared client/server) |
| `Assimalign.Cohesion.Database.Server` | Network front-end: sessions, auth handshake, frame pump |
| `Assimalign.Cohesion.Database.Client` | Shared client core: connection strings, pooling, protocol client |
| `Assimalign.Cohesion.Database.Security` | AuthN/AuthZ contracts (principals, roles, permissions) |
| `Assimalign.Cohesion.Database.Replication` | Shared replication contracts (WAL log-shipping seam) |
| `Assimalign.Cohesion.Database.Governance` | Quotas, tenancy boundaries, audit events |
| `Assimalign.Cohesion.Database.Hosting` | Host composition (`Host<TContext>`), the area's only DI seam |
| `Assimalign.Cohesion.Database.ApplicationModel` | Manifest-only orchestration resource + `AddDatabase(...)` |
| `Assimalign.Cohesion.Database.Embedded` | In-process embedded facade (post-MVP) |

## Dependencies on other areas

- `libraries/Core` — foundational primitives (everywhere)
- `libraries/Hosting` — host lifecycle + per-service execution menu (`Database.Hosting`)
- `libraries/Connections` — transport drivers for the server (`Database.Server`)
- `libraries/ApplicationModel` — orchestration contracts (`Database.ApplicationModel` only)
- `resources/Web` — private implementation detail of the root project (HTTP admin surface); hidden from consumers via `CohesionPrivateProjectReference`

## Building

The area solution is `Assimalign.Cohesion.Database.slnx`. Individual projects build with `dotnet build <project>` (build `build/Tasks` first in a fresh clone/worktree so the custom MSBuild tasks exist).
