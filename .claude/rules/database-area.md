---
paths:
  - "resources/Database/**"
  - "sdks/Assimalign.Cohesion.Sdk.Database/**"
  - "tooling/Assimalign.Cohesion.Database.Studio/**"
---

# Database Area (`resources/Database/**`)

Rules specific to the Database resource area. They apply to every project under
`resources/Database/`, and to `Sdk.Database` and Database Studio, which bind the area's types.
They layer on `resource-areas.md`, whose hosting-isolation rules (COHRES001–004, COHAM001) apply
here unchanged. The plan that moves the existing code onto these rules, phase by phase, is
`docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md` (epic #1255, phases #1256–#1264); the owner
decision is O34a in `docs/DEVELOPER_EXPERIENCE_DESIGN.md`.

## The concrete-first rule (owner decision, 2026-10-04)

> **Database public API is sealed concrete types. An abstract base exists only where a real
> variant set exists, or where `Database.Hosting` must stay model-agnostic under COHRES002.
> Interfaces survive only in the five places listed below.**

This replaces two general rules for this area: "Public APIs use interfaces" and "Interface-first
with a guided abstract base" (`general-rules.md`). The owner gave the direction on 2026-10-03.
On 2026-10-04 the owner approved the decision list in #1255, which names this deviation and
scopes it to this area, as `deviations.md` requires. The decision supersedes three earlier
ones: the 2026-10-02 decision that `ISqlDatabaseEngineBuilder` is "meant to be implemented
elsewhere", its 2026-10-03 narrowing, and #1232.

**Why the Database area differs from Web.** A Web feature plugs into one pipeline from many
packages, so the interface is the contract several implementations meet. A database engine is
specific to its runtime: its storage format, transaction kernel and wire protocol. There is no
second `SqlDatabaseEngine` to substitute, so an interface adds a public type and a cast at every
typed call site, and gives nothing in return. ADO.NET has the same shape: abstract bases with
public non-virtual members (`DbConnection`, `DbTransaction`) and sealed provider types
(`NpgsqlConnection`, `NpgsqlTransaction`). The change is about API shape, not speed: under
NativeAOT, the dispatch shape does not move engine throughput (the plan has the measurements).

**New code follows the rule now.** Add no new public interface to any Database project
(owner decision, 2026-10-04). Existing interfaces leave on the plan's schedule. Until a project's
phase lands, its existing interfaces stay, but nothing new is built on them.

## Where interfaces survive

Exactly five. After phase 8, `rg "public interface" resources/Database/**/src` lists only these.

| Interface | Project | Why it stays |
|---|---|---|
| `IDatabaseApplication` | Database (root) | The cross-area composition seam fixed by O34 for all 18 areas. `DatabaseApplication` already derives from `Host<DatabaseApplicationContext>`, and C# has single inheritance. |
| `IDatabaseApplicationBuilder` | Database (root) | Same seam. Root verbs are explicit-interface shims (`resource-areas.md`). Only its signatures are retyped (`AddEngine(DatabaseEngine)`). |
| `IDatabaseApplicationContext` | Database (root) | Same seam. `DatabaseApplicationContext` already derives from `HostContext`. |
| `IDatabaseResourceDescriptor` | Database.ApplicationModel | The 17-area `I<Area>ResourceDescriptor` pattern. It extends the library-owned `IResourceCommandDescriptor`. It is orchestration, not an engine model. |
| `IDatabaseApplicationTestFactory` | Database.Testing | Parity with Web.Testing's `IWebApplicationTestFactory`. It is the test-harness surface, not an engine model. |

Hosting-library contracts the area implements (`IHostService`, `IHealthContributor`,
`IResourceCommandHandler`) belong to `libraries/Hosting` and are not affected. BCL interfaces
(`IAsyncDisposable`, `IDisposable`, `IEquatable<T>` on value objects) are not public API
contracts in this sense and stay.

## Type shape

1. **Sealed by default.** Every public leaf is `sealed`: engines, databases, sessions,
   transactions, servers, builders, catalogs, clients and connections. When an internal
   implementation becomes the public type, it keeps an `internal` constructor. When
   construction must cross an assembly boundary, the type gets a public static factory on
   itself (`SqlClient.Create(...)`, `LockManager.Create()`). The
   `public static class X` + `public interface IX` + `internal DefaultX` triplet collapses into
   one `public sealed class X`. A factory may also sit on an abstract base and return its
   internal default leaf, the `Aes.Create()` shape (`ProtocolFrameReader.Create(...)`).
2. **Abstract only for a real variant set, or for Hosting.** A base exists when two or more
   implementations ship, or when `Database.Hosting` must treat every model alike under COHRES002.
   That covers the root engine, database, session, transaction, server and server session. A
   single implementation never gets an abstract base "for later".
3. **Constructor visibility follows assembly topology.**
   - A base whose leaves live in other shipped assemblies gets a `protected` constructor. That
     applies to the root bases and to child-root seams implemented in model assemblies. It
     cannot be closed, because `InternalsVisibleTo` between shipped libraries is banned
     (`general-rules.md`). Non-virtual members and sealed leaves keep it tight.
   - A base whose leaves all live in one assembly gets a `private protected` constructor. The
     owning project's own test assembly can still derive through its test-only
     `InternalsVisibleTo`; any other assembly fails with CS0122.
   - A base that need not be public at all is `internal abstract` (the storage strategies, the
     transaction log).
4. **Public members are non-virtual (NVI).** Public members own argument validation, disposed and
   state checks, the cancellation fast path, the state machine and telemetry. Each calls a
   `protected abstract …Core` or `…CoreAsync` member with no default body. `protected virtual`
   is allowed in three cases only:
   - an optional capability paired with a public `Supports*` flag (default `false`, a `Core`
     that throws `NotSupportedException`). `DatabaseInstance.SupportsSchemaProvisioning` is the
     only capability member allowed on `DatabaseInstance`.
   - a lifecycle hook behind a non-virtual public member, such as `DisposeAsyncCore` behind
     `DisposeAsync`, or a worker's trigger wait behind `Run`.
   - an observer hook. Observer hooks are `protected internal virtual` with empty bodies, so the
     owning client fires them and nobody else can.

   A virtual member whose default body falls back to another member is banned. That is the
   `DbConnection.OpenAsync` trap: the default async open calls the synchronous `Open`.
5. **No generic virtual methods.** NativeAOT never devirtualizes them. A generic method lives
   on a sealed type (`DatabaseConnection.ExecuteAsync<TResult>`, `SqlSchemaBuilder.Table<T>`) or
   in a static extension member (`GetEngine<TEngine>`). A generic *type* with virtual members
   (`DatabaseProtocolExchange<TResult>`) is fine.
6. **The base owns state as fields.** Values fixed at construction (name, model, id, isolation
   level, protocol version, principal, the owning engine or database) are non-virtual,
   field-backed getters. State changes go through protected, non-virtual methods on the base,
   never through overridable setters. A base that owns products filled in by a model assembly,
   such as an engine's workers and servers, accepts them through non-virtual `protected`
   attach methods. Those methods throw `InvalidOperationException` once composition is frozen
   after build.
7. **Typed surface without casts.**
   - A reference fixed at construction stays a base field. The leaf re-exposes it typed with
     `new`, backed by its own typed field: `public new SqlDatabaseEngine Engine => _engine;`.
     Neither path makes a virtual call.
   - A covariant override (`public override SqlCatalogSnapshot …`) is for members that are
     abstract for their own reasons, such as computed or leaf-specific state.
   - An async factory cannot be covariant, because `ValueTask<SqlDatabase>` does not convert to
     `ValueTask<DatabaseInstance>`. The leaf declares
     `public new ValueTask<SqlDatabaseSession> CreateSessionAsync(...)`, which awaits the
     **base public NVI member** and casts once. It never calls the `Core` member directly, which
     would skip the state machine and disposed checks. Reviewers check this on every `new`
     member.
8. **A behavior every model shares lives once, in the root base.** Each model used to carry its
   own copy: the explicit-transaction state machine (#1188/#1225), the "transaction already
   active" check, attach and dispose ordering. Each model now supplies only its vocabulary
   (error codes, exception translation) through a protected abstract member.
9. **Collections are never null.** A collection-valued member returns an empty collection, not
   `null`, when it has nothing to report. That includes `Diagnostics` on every result type.
   `QueryResult.Diagnostics` is the one standing exception, and it is removed in phase 8 (#1264).
10. **Names.** The replacement for `IDatabase` is `DatabaseInstance`, never `Database`. A type
    named `Database` breaks user code in a namespace such as `Acme.Database` with CS0118.
    `Storage.Storage` is the precedent for the pain. Leaves are `SqlDatabase`, `GraphDatabase`,
    and so on.
11. **Folders.** Abstract bases sit in the `src/` root or a feature folder, never in
    `Abstractions/`. A model project loses its `Abstractions/` folder with its last interface. A
    type promoted from `Internal/` moves out of it and declares the `RootNamespace`.

## Test doubles

- Hosting and Embedded doubles derive from the root bases and implement only the protected
  cores. This is why the root constructors are `protected`.
- Crash and fault-injection doubles derive from the strategy bases through the owning model's
  test-only `InternalsVisibleTo`.
- A double that was both a `Storage` and a record space is split: a `Storage` subclass plus a
  separate `TransactionRecordSpace`.
- Model behavior tests use real in-memory engines, as RavenDB does (`RunInMemory`), not fakes.
- Application developers lose interface mocking of clients. The substitute is an in-process
  engine, or `EmbeddedDatabase` behind a loopback server, which is also how Npgsql's sealed
  `NpgsqlConnection` is tested.

## Marking the deviation

Every public abstract base the program adds or keeps, every model engine and every model engine
builder carries this marker at its declaration. The plan lists them:

```csharp
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
```

Sealed leaves need no marker of their own, because this file covers them. Every PR in the series
carries the line `Deviates from interface-first (database-area.md)` in its change summary.

## How this relates to the other rules

- **`general-rules.md`, Access modifiers, rule 1** ("implementation classes: internal by
  default") still holds. A sealed public engine, session or client is the API itself, not an
  implementation hidden behind one. Helpers, strategies and per-model plumbing stay `internal`.
  The checklist line "Internal types are `internal`, not `public`" is satisfied the same way.
- **`InternalsVisibleTo` is for tests** applies unchanged. A project may grant only to its own
  test assembly. A foreign test that needs a kernel type uses its public surface, or is
  rewritten (the plan names each one).
- **Folder and namespace rules** apply unchanged. Only `Abstractions/` empties out.
- **`resource-areas.md`** applies unchanged. `Database.Hosting` still references only the area
  root and its own hosting family. The `I<Area>Application` seam (O34) is one of the five kept
  interfaces.
- Changing this rule beyond the plan is an owner decision. Record it in O34a and in this file.
